namespace Rag.Core;

/// <summary>Turns a PDF into chunks. One implementation per document profile (Strategy pattern).</summary>
public interface IIngestionStrategy
{
    DocumentProfile Profile { get; }

    Task<IngestionOutput> BuildChunksAsync(
        RagDocument document,
        PipelineOptions options,
        IProgress<IngestProgress>? progress,
        CancellationToken ct);
}

/// <param name="Parents">Whole sections for small-to-big (long reports); stored but not searched.</param>
public sealed record IngestionOutput(int PageCount, IReadOnlyList<Chunk> Chunks, IReadOnlyList<StageTrace> Trace, IReadOnlyList<Chunk>? Parents = null);

public interface IDocumentRepository
{
    Task<IReadOnlyList<RagDocument>> ListAsync(CancellationToken ct = default);
    Task<RagDocument?> GetAsync(int id, CancellationToken ct = default);
    Task<RagDocument?> FindByPathAsync(string filePath, CancellationToken ct = default);
    Task<int> CreateAsync(RagDocument document, CancellationToken ct = default);
    Task UpdateAsync(RagDocument document, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
}

public interface IChunkRepository
{
    Task ReplaceAsync(int documentId, string chunkSet, IReadOnlyList<Chunk> chunks, CancellationToken ct = default);
    Task<IReadOnlyList<Chunk>> GetAsync(int documentId, string chunkSet, bool includeEmbeddings, CancellationToken ct = default);
}

public sealed record StoredMessage(long Id, int DocumentId, string Role, string Content, string? MetaJson, DateTime CreatedAt);

public interface IMessageRepository
{
    Task<IReadOnlyList<StoredMessage>> ListAsync(int documentId, CancellationToken ct = default);
    Task AddAsync(int documentId, string role, string content, string? metaJson, CancellationToken ct = default);
    Task ClearAsync(int documentId, CancellationToken ct = default);
}

public sealed record EvalRunRecord(long Id, int DocumentId, DateTime CreatedAt, string ResultJson);

public interface IEvalRepository
{
    Task<long> AddAsync(int documentId, string resultJson, CancellationToken ct = default);
    Task<EvalRunRecord?> LatestAsync(int documentId, CancellationToken ct = default);
}

/// <summary>
/// Dense (vector) search. Swappable: exact cosine in C# over vectors loaded from SQL Server,
/// or pgvector inside PostgreSQL (HNSW index, `&lt;=&gt;` operator).
/// </summary>
public interface IDenseSearch
{
    string Name { get; }
    /// <summary>True when the chunk cache must hold the vectors (search runs in C#).</summary>
    bool NeedsVectorsInMemory { get; }
    /// <param name="sectionPrefixes">Only chunks whose section path starts with one of these (query routing); null = all.</param>
    Task<IReadOnlyList<(Guid ChunkId, double Score)>> SearchAsync(int documentId, string chunkSet, float[] query, int top,
        IReadOnlyList<string>? sectionPrefixes = null, CancellationToken ct = default);
}

public interface IDatabaseInitializer
{
    string Provider { get; }
    Task InitializeAsync(CancellationToken ct = default);
}
