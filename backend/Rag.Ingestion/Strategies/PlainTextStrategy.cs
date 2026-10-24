using System.Diagnostics;
using Rag.Core;
using Rag.Ingestion.Chunking;
using Rag.Ingestion.Layout;

namespace Rag.Ingestion.Strategies;

/// <summary>
/// Use case 1 (GPO Annual Report): narrative text on two-column pages.
/// Ingest: column-aware reading order, header/footer removal, text repair.
/// Chunk: whole sentences with overlap, tagged with the section heading and page.
/// </summary>
public sealed class PlainTextStrategy : IIngestionStrategy
{
    public DocumentProfile Profile => DocumentProfile.PlainText;

    public Task<IngestionOutput> BuildChunksAsync(
        RagDocument document, PipelineOptions options, IProgress<IngestProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        progress?.Report(new IngestProgress(Stages.Ingest, 5, "Reading PDF layout"));

        var layout = new PdfLayoutReader().Read(document.FilePath, new LayoutOptions(
            options.ColumnReadingOrder, options.HeadingsFromFonts, options.RepairLigatures, options.PairLabelValues));
        var s = layout.Stats;
        var ingestTrace = new StageTrace(Stages.Ingest, "Reading order for columns", options.ColumnReadingOrder,
            $"{s.Pages} pages, {s.Blocks} text blocks. Removed {s.DroppedMargin} header/footer/page-number blocks " +
            $"and {s.DroppedOffPage} off-page blocks. Found {s.Headings} headings and {s.Asides} captions/callouts.",
            ElapsedMs: sw.ElapsedMilliseconds);

        ct.ThrowIfCancellationRequested();
        sw.Restart();
        progress?.Report(new IngestProgress(Stages.Chunk, 25, "Chunking by sentence"));

        var chunks = new SentenceChunker(new SentenceChunkerOptions()).Chunk(layout.Segments, document.Id);
        var text = chunks.Where(c => c.ContentType == ContentTypes.Text).ToList();
        var chunkTrace = new StageTrace(Stages.Chunk, "Whole sentences + overlap", options.SentenceChunks,
            $"{chunks.Count} chunks ({text.Count} body text, {chunks.Count - text.Count} captions/callouts). " +
            $"Average {(text.Count == 0 ? 0 : (int)text.Average(c => c.TokenCount))} tokens; overlap of up to 2 sentences.",
            ElapsedMs: sw.ElapsedMilliseconds);

        return Task.FromResult(new IngestionOutput(s.Pages, chunks, [ingestTrace, chunkTrace]));
    }
}
