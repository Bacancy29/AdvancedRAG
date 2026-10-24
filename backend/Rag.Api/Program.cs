using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Rag.Core;
using Rag.Generation;
using Rag.Hosting;
using Rag.Retrieval;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddUserSecrets("advanced-rag");
builder.Services.AddAdvancedRag(builder.Configuration);
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 200 * 1024 * 1024);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 200 * 1024 * 1024);
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [])
    .AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors();
await app.Services.GetRequiredService<StartupTasks>().RunAsync();
// Load indexes, reranker and embedding model in the background so the first question is not the slow one.
app.Lifetime.ApplicationStarted.Register(() => _ = Task.Run(() => app.Services.GetRequiredService<WarmUp>().RunAsync()));

var api = app.MapGroup("/api");

// ---------- System ----------

api.MapGet("/health", (ChatModelOptions chat, EmbeddingService embeddings, IReranker reranker, IDatabaseInitializer database, IDenseSearch dense) => new
{
    Database = database.Provider,
    VectorSearch = dense.Name,
    ChatModel = $"{chat.Provider}:{chat.Model}",
    ChatConfigured = chat.Provider.Equals("ollama", StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrWhiteSpace(chat.ApiKey) || Environment.GetEnvironmentVariable("GEMINI_API_KEY") is not null,
    EmbeddingModel = embeddings.ModelName,
    Reranker = reranker.IsAvailable ? reranker.Name : null,
});

api.MapGet("/pipeline", () => PipelineCatalog.Build());

// ---------- Documents ----------

api.MapGet("/documents", async (IDocumentRepository docs, IngestionJobs jobs) =>
    (await docs.ListAsync()).Select(d => ToDto(d, jobs)));

api.MapGet("/documents/{id:int}", async (int id, IDocumentRepository docs, IngestionJobs jobs) =>
    await docs.GetAsync(id) is { } d ? Results.Ok(ToDto(d, jobs)) : Results.NotFound());

api.MapPost("/documents", async (HttpRequest request, IDocumentRepository docs) =>
{
    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file is null || !file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Upload a PDF file." });
    if (!Enum.TryParse<DocumentProfile>(form["profile"], out var profile))
        return Results.BadRequest(new { error = "Choose a profile: PlainText, LongReport or Visual." });

    var folder = AppPaths.Resolve("data/uploads");
    Directory.CreateDirectory(folder);
    var path = Path.Combine(folder, $"{Guid.NewGuid():N}-{Path.GetFileName(file.FileName)}");
    await using (var stream = File.Create(path)) await file.CopyToAsync(stream);

    var name = string.IsNullOrWhiteSpace(form["name"]) ? Path.GetFileNameWithoutExtension(file.FileName) : form["name"].ToString();
    var id = await docs.CreateAsync(new RagDocument { Name = name, FilePath = path, Profile = profile, Status = DocumentStatus.NotIngested });
    return Results.Created($"/api/documents/{id}", new { id });
}).DisableAntiforgery();

api.MapDelete("/documents/{id:int}", async (int id, IDocumentRepository docs, ChunkIndexCache cache) =>
{
    await docs.DeleteAsync(id);
    cache.Invalidate(id);
    return Results.NoContent();
});

api.MapPost("/documents/{id:int}/ingest", (int id, IngestionJobs jobs) =>
    jobs.Start(id) ? Results.Accepted() : Results.Conflict(new { error = "Ingestion already running." }));

api.MapGet("/documents/{id:int}/chunks", async (int id, string? set, IChunkRepository chunks) =>
    (await chunks.GetAsync(id, set ?? ChunkSets.Advanced, includeEmbeddings: false))
        .Select(c => new { c.Id, c.ChunkIndex, c.ContentType, c.PageNumber, c.PageEnd, c.SectionPath, c.TokenCount, c.Text }));

// Rendered page images (use case 3): chart source cards show the page the vision model described.
api.MapGet("/documents/{id:int}/pages/{page:int}.png", (int id, int page) =>
{
    var file = AppPaths.Resolve(Path.Combine("data", "pages", id.ToString(), $"p{page}.png"));
    return File.Exists(file) ? Results.File(file, "image/png") : Results.NotFound();
});

// ---------- Chat ----------

api.MapGet("/documents/{id:int}/messages", async (int id, IMessageRepository messages) =>
    (await messages.ListAsync(id)).Select(m => new
    {
        m.Id, m.Role, m.Content, m.CreatedAt,
        Meta = m.MetaJson is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(m.MetaJson),
    }));

api.MapDelete("/documents/{id:int}/messages", async (int id, IMessageRepository messages) =>
{
    await messages.ClearAsync(id);
    return Results.NoContent();
});

// Server-Sent Events: "token" events while the answer is generated, then one "done" event with citations + trace.
api.MapPost("/chat", async (ChatRequest req, RagService rag, HttpContext http, CancellationToken ct) =>
{
    http.Response.Headers.ContentType = "text/event-stream";
    http.Response.Headers.CacheControl = "no-cache";

    async Task Send(string evt, object data)
    {
        await http.Response.WriteAsync($"event: {evt}\ndata: {JsonSerializer.Serialize(data, RagService.Json)}\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }

    try
    {
        await foreach (var e in rag.AskStreamAsync(req.DocumentId, req.Question, req.Naive, saveHistory: !req.Naive, ct))
        {
            if (e is RetrievedEvent r) await Send("retrieved", new { r.Citations, r.Trace });
            else if (e is TokenEvent t) await Send("token", new { t.Text });
            else if (e is CompletedEvent done) await Send("done", done.Answer);
        }
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
        await Send("error", new { error = ex.Message });
    }
});

// ---------- Evaluation ----------

api.MapGet("/documents/{id:int}/eval/questions", async (int id, EvaluationService eval) =>
{
    try { return Results.Ok(await eval.LoadSetAsync(id)); }
    catch (FileNotFoundException) { return Results.NotFound(new { error = "No evaluation set for this document yet." }); }
});

api.MapPost("/documents/{id:int}/eval", async (int id, bool? answers, bool? resume, EvaluationService eval, CancellationToken ct) =>
    Results.Ok(await eval.RunAsync(id, answers ?? true, ct: ct, resume: resume ?? false)));

// Naive vs advanced for every document, from each one's latest evaluation run.
api.MapGet("/scorecard", async (IDocumentRepository docs, IEvalRepository runs) =>
{
    var rows = new List<object>();
    foreach (var d in await docs.ListAsync())
    {
        if (await runs.LatestAsync(d.Id) is not { } run) continue;
        var r = JsonSerializer.Deserialize<EvalReport>(run.ResultJson, RagService.Json)!;
        if (r.Ladder.Count == 0) continue;
        rows.Add(new
        {
            DocumentId = d.Id, d.Name, Profile = d.Profile.ToString(), r.CreatedAt, r.ChatModel,
            NaiveMrr = r.Ladder[0].Mrr, AdvancedMrr = r.Ladder[^1].Mrr,
            NaiveHitAt5 = r.Ladder[0].HitAt5, AdvancedHitAt5 = r.Ladder[^1].HitAt5,
            SectionHit = r.Ladder[^1].SectionHit,
            r.Passed, r.Total, r.RequiredPassed,
            Unanswered = r.Answers.Count(a => a.Error is not null),
        });
    }
    return rows;
});

api.MapGet("/documents/{id:int}/eval/latest",async (int id, IEvalRepository runs) =>
    await runs.LatestAsync(id) is { } run
        ? Results.Content(run.ResultJson, "application/json")
        : Results.NotFound());

app.Run();

static object ToDto(RagDocument d, IngestionJobs jobs) => new
{
    d.Id, d.Name, Profile = d.Profile.ToString(), Status = d.Status.ToString(), d.PageCount, d.ChunkCount,
    d.EmbeddingModel, d.Error, d.CreatedAt, d.IngestedAt,
    FileName = Path.GetFileName(d.FilePath),
    HasEvalSet = File.Exists(EvaluationService.EvalSetPath(d)),
    Progress = jobs.Get(d.Id),
};

public sealed record ChatRequest(int DocumentId, string Question, bool Naive = false);
