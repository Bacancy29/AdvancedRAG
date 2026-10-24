namespace Rag.Core;

/// <summary>
/// Which techniques run for a document. Stages that a profile does not need are switched off,
/// so new documents plug in by choosing a profile rather than changing pipeline code.
/// </summary>
public sealed record PipelineOptions
{
    // 1. Ingest
    public bool ColumnReadingOrder { get; init; }
    public bool RepairLigatures { get; init; }
    public bool PairLabelValues { get; init; }
    public bool HeadingsFromFonts { get; init; }
    public bool TableRebuild { get; init; }
    public bool FootnoteLinks { get; init; }
    public bool VisionDescriptions { get; init; }

    // 2. Chunk
    public bool SentenceChunks { get; init; }
    public bool HeadingPathEmbedded { get; init; }
    public bool ParentChild { get; init; }
    public bool ChapterSummaries { get; init; }

    // 3. Query
    public QueryMode Query { get; init; } = QueryMode.None;

    // 4. Retrieve
    public bool Hybrid { get; init; }
    public bool MetadataFilter { get; init; }

    // 5. Post-process
    public bool Rerank { get; init; }
    public bool SmallToBig { get; init; }

    // Which chunk set to search
    public string ChunkSet { get; init; } = ChunkSets.Advanced;

    public static PipelineOptions ForProfile(DocumentProfile profile) => profile switch
    {
        DocumentProfile.PlainText => new()
        {
            ColumnReadingOrder = true,
            RepairLigatures = true,
            PairLabelValues = true,
            HeadingsFromFonts = true,
            SentenceChunks = true,
            Hybrid = true,
            Rerank = true,
        },
        DocumentProfile.LongReport => new()
        {
            ColumnReadingOrder = true,
            RepairLigatures = true,
            HeadingsFromFonts = true,
            SentenceChunks = true,
            HeadingPathEmbedded = true,
            ParentChild = true,
            ChapterSummaries = true,
            Query = QueryMode.Rules,
            Hybrid = true,
            MetadataFilter = true,
            Rerank = true,
            SmallToBig = true,
        },
        DocumentProfile.Visual => new()
        {
            ColumnReadingOrder = true,
            RepairLigatures = true,
            HeadingsFromFonts = true,
            TableRebuild = true,
            FootnoteLinks = true,
            VisionDescriptions = true,
            SentenceChunks = true,
            Hybrid = true,
            Rerank = true,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };

    /// <summary>Baseline: fixed-size chunks, dense search only, no reranking.</summary>
    public static PipelineOptions Naive => new() { ChunkSet = ChunkSets.Naive };
}
