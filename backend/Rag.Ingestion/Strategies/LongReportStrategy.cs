using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Rag.Core;
using Rag.Ingestion.Chunking;
using Rag.Ingestion.Layout;

namespace Rag.Ingestion.Strategies;

/// <summary>
/// Use case 2 (World Bank Global Economic Prospects, ~200 pages): long and hierarchical.
/// Ingest: chapters from bookmarks, sections and sub-sections from fonts, boxed articles as their own stream,
/// table-of-contents pages skipped. Chunk: parents (whole sections) and children (searched), each tagged with
/// its full heading path; plus one LLM summary per chapter.
/// </summary>
public sealed class LongReportStrategy(ChapterSummarizer summarizer, ILogger<LongReportStrategy> logger) : IIngestionStrategy
{
    public DocumentProfile Profile => DocumentProfile.LongReport;

    public async Task<IngestionOutput> BuildChunksAsync(
        RagDocument document, PipelineOptions options, IProgress<IngestProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        progress?.Report(new IngestProgress(Stages.Ingest, 5, "Reading bookmarks and page layout"));

        var outline = DocumentOutline.Read(document.FilePath);
        var layout = new PdfLayoutReader().Read(document.FilePath, new LayoutOptions(
            options.ColumnReadingOrder, options.HeadingsFromFonts, options.RepairLigatures,
            Structured: true, SkipPages: outline.ContentsPages));
        var s = layout.Stats;
        var ingestTrace = new StageTrace(Stages.Ingest, "Headings from fonts + bookmarks", true,
            $"{s.Pages} pages. Chapters from bookmarks: {outline.Chapters.Count}; {s.Headings} section headings from fonts. " +
            $"Skipped {s.SkippedPages} table-of-contents pages, removed {s.DroppedMargin} header/footer blocks and " +
            $"{s.DroppedFragments} chart/table number fragments. Boxed articles kept as separate streams.",
            ElapsedMs: sw.ElapsedMilliseconds);

        ct.ThrowIfCancellationRequested();
        sw.Restart();
        progress?.Report(new IngestProgress(Stages.Chunk, 20, "Building parent sections and child chunks"));
        var result = new ParentChildChunker(new ParentChildOptions()).Chunk(layout.Segments, outline, document.Id);
        var children = result.Children;

        var summaries = 0;
        if (options.ChapterSummaries)
        {
            var chapters = result.Parents
                .GroupBy(p => p.SectionPath.Split(" > ")[0])
                .Where(g => g.Key.StartsWith("Chapter ", StringComparison.Ordinal))
                .ToList();
            foreach (var (g, i) in chapters.Select((g, i) => (g, i)))
            {
                progress?.Report(new IngestProgress(Stages.Chunk, 25 + 10 * i / Math.Max(1, chapters.Count), $"Summarizing {g.Key}"));
                try
                {
                    var chapterText = string.Join("\n\n", g.OrderBy(p => p.ChunkIndex).Select(p => p.Text));
                    var summary = await summarizer.SummarizeAsync(document.Name, g.Key, chapterText, ct);
                    if (summary.Length == 0) continue;
                    var first = g.Min(p => p.PageNumber);
                    children.Add(new Chunk
                    {
                        DocumentId = document.Id,
                        ContentType = ContentTypes.Summary,
                        PageNumber = first,
                        PageEnd = g.Max(p => p.PageEnd),
                        SectionPath = $"{g.Key} > Chapter summary",
                        Text = $"Summary of {g.Key}: {summary}",
                        TokenCount = TokenEstimator.Estimate(summary),
                    });
                    summaries++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Summaries help broad questions but are optional; never fail the ingestion over them.
                    logger.LogWarning("Summary for {Chapter} skipped: {Error}", g.Key, ex.Message.Split('\n')[0]);
                }
            }
            for (var i = 0; i < children.Count; i++) children[i].ChunkIndex = i;
        }

        var text = children.Where(c => c.ContentType == ContentTypes.Text).ToList();
        var chunkTrace = new StageTrace(Stages.Chunk, "Parents and children + heading path", true,
            $"{result.Parents.Count} parent sections (~{(result.Parents.Count == 0 ? 0 : (int)result.Parents.Average(p => p.TokenCount))} tokens) → " +
            $"{text.Count} child chunks (~{(text.Count == 0 ? 0 : (int)text.Average(c => c.TokenCount))} tokens), " +
            $"{children.Count(c => c.ContentType == ContentTypes.Aside)} figure/caption chunks, {summaries} chapter summaries. " +
            "Each child is embedded with its heading path.",
            ElapsedMs: sw.ElapsedMilliseconds);

        return new IngestionOutput(s.Pages, children, [ingestTrace, chunkTrace], result.Parents);
    }
}
