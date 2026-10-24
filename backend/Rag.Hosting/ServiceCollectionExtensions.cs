using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rag.Core;
using Npgsql;
using Rag.Data;
using Rag.Data.Postgres;
using Rag.Generation;
using Rag.Ingestion;
using Rag.Ingestion.Strategies;
using Rag.Ingestion.Visuals;
using Rag.Retrieval;

namespace Rag.Hosting;

public sealed class DatabaseOptions
{
    public string Provider { get; set; } = "SqlServer";
    /// <summary>Embedding size; the pgvector column is vector(Dimensions).</summary>
    public int Dimensions { get; set; } = 768;
    /// <summary>Kept in user secrets, not appsettings.</summary>
    public string? PostgresPassword { get; set; }
}

public sealed class SeedDocument
{
    public string Name { get; set; } = "";
    public string FilePath { get; set; } = "";
    public DocumentProfile Profile { get; set; }
}

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAdvancedRag(this IServiceCollection services, IConfiguration config)
    {
        var chat = config.GetSection("AI:Chat").Get<ChatModelOptions>() ?? new();
        var embedding = config.GetSection("AI:Embedding").Get<EmbeddingOptions>() ?? new();
        embedding.CacheDirectory = AppPaths.Resolve(embedding.CacheDirectory ?? "data/cache/embeddings");
        var routing = config.GetSection("Routing").Get<RoutingOptions>() ?? new();
        var rag = config.GetSection("Rag").Get<RagOptions>() ?? new();
        var reranker = config.GetSection("Reranker").Get<RerankerOptions>() ?? new();
        reranker.ModelPath = AppPaths.Resolve(reranker.ModelPath);
        reranker.VocabPath = AppPaths.Resolve(reranker.VocabPath);

        services.AddSingleton(chat).AddSingleton(embedding).AddSingleton(rag).AddSingleton(reranker).AddSingleton(routing);

        // Data: "Database:Provider" = SqlServer (vectors as bytes, searched in C#) or Postgres (pgvector)
        var database = config.GetSection("Database").Get<DatabaseOptions>() ?? new();
        services.AddSingleton(database);
        if (database.Provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
        {
            var pg = new NpgsqlConnectionStringBuilder(config.GetConnectionString("RagPostgres")
                ?? throw new InvalidOperationException("ConnectionStrings:RagPostgres missing"));
            if (!string.IsNullOrEmpty(database.PostgresPassword)) pg.Password = database.PostgresPassword;

            services.AddSingleton(new PgDatabase(pg.ConnectionString, database.Dimensions));
            services.AddSingleton<IDatabaseInitializer, PgDbInitializer>();
            services.AddSingleton<IDocumentRepository, PgDocumentRepository>();
            services.AddSingleton<IChunkRepository, PgChunkRepository>();
            services.AddSingleton<IMessageRepository, PgMessageRepository>();
            services.AddSingleton<IEvalRepository, PgEvalRepository>();
            services.AddSingleton<IDenseSearch, PgVectorDenseSearch>();
            services.AddSingleton(sp => new ChunkIndexCache(sp.GetRequiredService<IChunkRepository>(), loadVectors: false));
        }
        else
        {
            var connection = config.GetConnectionString("Rag") ?? throw new InvalidOperationException("ConnectionStrings:Rag missing");
            services.AddSingleton(new SqlConnectionFactory(connection));
            services.AddSingleton<IDatabaseInitializer, DbInitializer>();
            services.AddSingleton<IDocumentRepository, DocumentRepository>();
            services.AddSingleton<IChunkRepository, ChunkRepository>();
            services.AddSingleton<IMessageRepository, MessageRepository>();
            services.AddSingleton<IEvalRepository, EvalRepository>();
            services.AddSingleton<IDenseSearch, InMemoryDenseSearch>();
            services.AddSingleton(sp => new ChunkIndexCache(sp.GetRequiredService<IChunkRepository>(), loadVectors: true));
        }

        // AI providers (lazy: a missing Gemini key only fails when chat is actually used)
        services.AddSingleton(_ => AiClientFactory.CreateEmbedding(embedding));
        services.AddSingleton<IChatClient>(_ => new LazyChatClient(() => AiClientFactory.CreateChat(chat)));
        services.AddSingleton<EmbeddingService>();

        // Ingestion: one strategy per document profile
        services.AddSingleton<IIngestionStrategy, PlainTextStrategy>();
        services.AddSingleton<IIngestionStrategy, LongReportStrategy>();
        services.AddSingleton<IIngestionStrategy, VisualStrategy>();
        services.AddSingleton(sp => new VisionDescriber(sp.GetRequiredService<IChatClient>(), AppPaths.Resolve("data/pages"), AppPaths.Resolve("data/cache/vision")));
        services.AddSingleton(sp => new ChapterSummarizer(sp.GetRequiredService<IChatClient>(), AppPaths.Resolve("data/cache/summaries")));
        services.AddSingleton(sp =>
        {
            var service = ActivatorUtilities.CreateInstance<IngestionService>(sp);
            var cache = sp.GetRequiredService<ChunkIndexCache>();
            service.DocumentIngested += cache.Invalidate;
            return service;
        });
        services.AddSingleton<IngestionJobs>();

        // Retrieval + generation
        services.AddSingleton<IReranker>(sp => new CrossEncoderReranker(reranker, sp.GetRequiredService<ILogger<CrossEncoderReranker>>()));
        services.AddSingleton<QueryRouter>();
        services.AddSingleton<RetrievalPipeline>();
        services.AddSingleton<AnswerGenerator>();
        services.AddSingleton<RagService>();
        services.AddSingleton<EvaluationService>();

        services.AddSingleton(config.GetSection("Documents").Get<List<SeedDocument>>() ?? []);
        services.AddSingleton<StartupTasks>();
        services.AddSingleton<WarmUp>();
        return services;
    }
}

/// <summary>Creates the database and registers the configured documents on first run.</summary>
public sealed class StartupTasks(IDatabaseInitializer db, IDocumentRepository documents, List<SeedDocument> seeds, ILogger<StartupTasks> logger)
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        await db.InitializeAsync(ct);
        foreach (var seed in seeds)
        {
            var path = AppPaths.Resolve(seed.FilePath);
            if (!File.Exists(path))
            {
                logger.LogWarning("Seed document not found: {Path}", path);
                continue;
            }
            if (await documents.FindByPathAsync(path, ct) is not null) continue;
            await documents.CreateAsync(new RagDocument
            {
                Name = seed.Name, FilePath = path, Profile = seed.Profile, Status = DocumentStatus.NotIngested,
            }, ct);
            logger.LogInformation("Registered document {Name}", seed.Name);
        }
    }
}
