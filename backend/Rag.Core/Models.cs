namespace Rag.Core;

public sealed class RagDocument
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string FilePath { get; set; } = "";
    public DocumentProfile Profile { get; set; }
    public DocumentStatus Status { get; set; }
    public int PageCount { get; set; }
    public int ChunkCount { get; set; }
    public string? EmbeddingModel { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? IngestedAt { get; set; }
}

public sealed class Chunk
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int DocumentId { get; set; }
    public string ChunkSet { get; set; } = ChunkSets.Advanced;
    public Guid? ParentId { get; set; }
    public int ChunkIndex { get; set; }
    public string ContentType { get; set; } = ContentTypes.Text;
    public int PageNumber { get; set; }
    public int PageEnd { get; set; }
    public string SectionPath { get; set; } = "";
    /// <summary>Text shown to the LLM and the user.</summary>
    public string Text { get; set; } = "";
    public int TokenCount { get; set; }
    public float[] Embedding { get; set; } = [];
}

public sealed record ScoredChunk(Chunk Chunk, double Score);

public sealed record TraceHit(Guid ChunkId, int Rank, double Score, int Page, string Section, string Preview);

/// <summary>What one pipeline stage did for one question. Drives the UI's pipeline view.</summary>
public sealed record StageTrace(
    string Stage,
    string Technique,
    bool Enabled,
    string Summary,
    IReadOnlyList<TraceHit>? Hits = null,
    long ElapsedMs = 0);

public sealed record Citation(
    int Number,
    Guid ChunkId,
    int Page,
    string Section,
    string ContentType,
    string Text,
    double Score,
    bool Cited);

public sealed record RetrievalResult(
    IReadOnlyList<ScoredChunk> Passages,
    IReadOnlyList<ScoredChunk> Ranked,
    IReadOnlyList<StageTrace> Trace,
    bool BelowThreshold);

public sealed record RagAnswer(
    string Answer,
    bool Refused,
    IReadOnlyList<Citation> Citations,
    IReadOnlyList<StageTrace> Trace);

public sealed record IngestProgress(string Stage, int Percent, string Message);
