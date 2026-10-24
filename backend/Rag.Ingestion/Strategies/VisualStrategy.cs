using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Rag.Core;
using Rag.Ingestion.Chunking;
using Rag.Ingestion.Footnotes;
using Rag.Ingestion.Layout;
using Rag.Ingestion.Visuals;
using UglyToad.PdfPig;

namespace Rag.Ingestion.Strategies;

/// <summary>
/// Use case 3 (Apple iMac Product Environmental Report): short, but the facts sit in tables, charts and footnotes.
/// <list type="bullet">
/// <item>Tables: cells aligned in rows and columns rebuilt as Markdown, one chunk per table.</item>
/// <item>Footnotes: superscript references become [^n]; the endnote text is attached to the chunk citing it.</item>
/// <item>Charts: pages with figures are rendered and described by a vision model (cached), one chunk per page.</item>
/// </list>
/// </summary>
public sealed class VisualStrategy(VisionDescriber vision, ILogger<VisualStrategy> logger) : IIngestionStrategy
{
    public DocumentProfile Profile => DocumentProfile.Visual;

    public async Task<IngestionOutput> BuildChunksAsync(
        RagDocument document, PipelineOptions options, IProgress<IngestProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        progress?.Report(new IngestProgress(Stages.Ingest, 5, "Reading layout, tables and footnote markers"));

        var layout = new PdfLayoutReader().Read(document.FilePath, new LayoutOptions(
            options.ColumnReadingOrder, options.HeadingsFromFonts, options.RepairLigatures,
            Structured: true, DetectTables: options.TableRebuild, FootnoteMarkers: options.FootnoteLinks));
        var chunks = new SentenceChunker(new SentenceChunkerOptions()).Chunk(layout.Segments, document.Id);

        // Footnotes: endnote text, keyed by number, attached to every chunk that cites it.
        var links = 0;
        var notes = new Dictionary<string, string>();
        if (options.FootnoteLinks)
        {
            notes = FootnoteLinker.ReadEndnotes(EndnotesText(layout.Segments));
            links = FootnoteLinker.Link(chunks, notes);
        }
        var s = layout.Stats;
        var ingestTrace = new StageTrace(Stages.Ingest, "Tables, footnotes and figures", true,
            $"{s.Pages} pages. Rebuilt {s.Tables} tables as Markdown; found {s.FootnoteMarkers} footnote references and " +
            $"{notes.Count} endnotes, attached {links} footnotes to the chunks that cite them.",
            ElapsedMs: sw.ElapsedMilliseconds);

        // Charts: render the pages that hold figures and let the vision model describe them.
        var figures = 0;
        if (options.VisionDescriptions)
        {
            var pages = FigurePages(document.FilePath);
            foreach (var (page, i) in pages.Select((p, i) => (p, i)))
            {
                progress?.Report(new IngestProgress(Stages.Ingest, 15 + 20 * i / Math.Max(1, pages.Count), $"Describing figures on page {page}"));
                try
                {
                    var image = vision.RenderPage(document.FilePath, document.Id, page);
                    var description = await vision.DescribeAsync(image, ct);
                    if (description is null) continue;
                    var section = chunks.Where(c => c.PageNumber == page).Select(c => c.SectionPath).FirstOrDefault(p => p.Length > 0)
                        ?? chunks.LastOrDefault(c => c.PageNumber < page)?.SectionPath ?? "";
                    chunks.Add(new Chunk
                    {
                        DocumentId = document.Id,
                        ContentType = ContentTypes.Chart,
                        PageNumber = page,
                        PageEnd = page,
                        SectionPath = section,
                        Text = $"Figures on page {page}: {description}",
                        TokenCount = TokenEstimator.Estimate(description),
                    });
                    figures++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Vision is an addition; the document stays usable without it.
                    logger.LogWarning("Figure description for page {Page} skipped: {Error}", page, ex.Message.Split('\n')[0]);
                }
            }
        }
        for (var i = 0; i < chunks.Count; i++) chunks[i].ChunkIndex = i;

        var byType = chunks.GroupBy(c => c.ContentType).ToDictionary(g => g.Key, g => g.Count());
        var chunkTrace = new StageTrace(Stages.Chunk, "One chunk per element", true,
            $"{chunks.Count} chunks: {byType.GetValueOrDefault(ContentTypes.Text)} text, {byType.GetValueOrDefault(ContentTypes.Table)} tables, " +
            $"{figures} figure descriptions (vision), {byType.GetValueOrDefault(ContentTypes.Footnote)} endnote, " +
            $"{byType.GetValueOrDefault(ContentTypes.Aside)} caption/callout chunks.",
            ElapsedMs: sw.ElapsedMilliseconds);

        return new IngestionOutput(s.Pages, chunks, [ingestTrace, chunkTrace]);
    }

    /// <summary>The running text after an "Endnotes" heading.</summary>
    private static string EndnotesText(IReadOnlyList<LayoutSegment> segments)
    {
        var start = segments.ToList().FindIndex(s => s.Kind == SegmentKind.Heading && FootnoteLinker.IsEndnoteSection(s.Text));
        return start < 0 ? "" : string.Join(" ", segments.Skip(start + 1)
            .Where(s => s.Kind is SegmentKind.Body or SegmentKind.Aside)
            .Select(s => s.Text));
    }

    /// <summary>
    /// Pages worth a vision call: vector drawings or images (charts, diagrams, infographics), excluding pages
    /// that are mostly small running text. Text-only pages are skipped to save calls.
    /// </summary>
    public static List<int> FigurePages(string pdfPath)
    {
        using var pdf = PdfDocument.Open(pdfPath);
        var result = new List<int>();
        foreach (var page in pdf.GetPages())
        {
            var smallWords = page.GetWords().Count(w => w.Letters.Count > 0 && w.Letters[0].PointSize <= 8);
            var drawings = page.Paths.Count;
            var images = page.GetImages().Count();
            // Graphics on the page, but not a page of small running text (endnotes, definitions).
            if ((drawings >= 40 || images > 0) && smallWords <= 300) result.Add(page.Number);
        }
        return result;
    }
}
