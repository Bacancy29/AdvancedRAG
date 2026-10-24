using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace Rag.Ingestion.Layout;

/// <param name="Structured">Long reports: heading levels from font size/family, multi-line headings, boxed-article
/// page markers, and numeric chart/table fragments dropped. Off for plain-text documents.</param>
/// <param name="SkipPages">Pages to ignore entirely (e.g. the table of contents).</param>
/// <param name="DetectTables">Rebuild tables from small text cells aligned in rows and columns (Markdown).</param>
/// <param name="FootnoteMarkers">Turn tiny superscript numbers into [^n] markers so footnotes can be linked.</param>
public sealed record LayoutOptions(
    bool ColumnReadingOrder, bool HeadingsFromFonts, bool RepairText, bool PairLabelValues = false,
    bool Structured = false, IReadOnlySet<int>? SkipPages = null,
    bool DetectTables = false, bool FootnoteMarkers = false);

/// <summary>
/// Reads a PDF into an ordered list of headings, body text and asides.
/// The key job for two-column reports: read the left column fully before the right one,
/// and keep captions/callouts out of the main sentence flow so sentences are never broken.
/// </summary>
public sealed partial class PdfLayoutReader
{
    private const double TopMargin = 60;     // points from the top edge
    private const double BottomMargin = 90;  // points from the bottom edge
    private const int RepeatThreshold = 3;   // margin text seen on this many pages = running header/footer

    public DocumentLayout Read(string pdfPath, LayoutOptions options)
    {
        using var pdf = PdfDocument.Open(pdfPath);
        var pages = pdf.GetPages().Select(ReadPage).ToList();

        // Running headers/footers repeat across pages; count them once over the whole document.
        var repeated = pages
            .SelectMany(p => p.Blocks.Where(b => b.InMargin).Select(b => (p.Number, b.MarginKey)).Distinct())
            .GroupBy(x => x.MarginKey)
            .Where(g => g.Key.Length > 0 && g.Count() >= RepeatThreshold)
            .Select(g => g.Key)
            .ToHashSet();

        var segments = new List<LayoutSegment>();
        int blocks = 0, droppedMargin = 0, droppedOffPage = 0, headings = 0, asides = 0, fragments = 0, skipped = 0, tables = 0, markers = 0;

        foreach (var page in pages)
        {
            if (options.SkipPages?.Contains(page.Number) == true)
            {
                skipped++;
                continue;
            }
            blocks += page.Blocks.Count + page.OffPageCount;
            droppedOffPage += page.OffPageCount;

            var kept = new List<PageBlock>();
            foreach (var b in page.Blocks)
            {
                if (b.InMargin && (repeated.Contains(b.MarginKey) || b.IsPageNumber || b.FontSize <= 6.5))
                {
                    droppedMargin++;
                    continue;
                }
                kept.Add(b);
            }

            if (options.FootnoteMarkers)
            {
                kept = kept.Select(b => MarkFootnotes(b, SizeOf(page.BodyFont), ref markers)).ToList();
            }

            var pageTables = new List<string>();
            if (options.DetectTables)
            {
                (kept, pageTables) = ExtractTables(kept, options);
                tables += pageTables.Count;
            }

            if (options.PairLabelValues) kept = PairLabelsWithValues(kept);

            string? boxTitle = null;
            if (options.Structured)
            {
                // A boxed-article page header ("BOX 1.1 … (continued)") marks the page as (mostly) part of that box.
                var marker = kept.FirstOrDefault(b => BoxRegex().IsMatch(b.Lines[0].Text.Trim()));
                if (marker is not null)
                {
                    kept.Remove(marker);
                    boxTitle = ContinuedRegex().Replace(Clean(JoinLines(marker.Lines, options), options), "").Trim();
                }
                // Chart axis labels and scattered table cells carry no sentences; they only add noise.
                var before = kept.Count;
                kept = kept.Where(b => !IsNumericFragment(b, SizeOf(page.BodyFont))).ToList();
                fragments += before - kept.Count;
            }

            var ordered = options.ColumnReadingOrder ? ColumnOrder(kept, page.Width) : TopDown(kept);

            var kinds = ordered.Select(b => options.Structured
                ? ClassifyStructured(b, page.BodyFont)
                : Classify(b, page.BodyFont, options.HeadingsFromFonts)).ToList();
            for (var i = 0; i < kinds.Count && !options.Structured; i++)
            {
                // A "heading" followed by non-body text is a chart or table title, not a section heading.
                if (kinds[i] == SegmentKind.Heading && i + 1 < kinds.Count && kinds[i + 1] == SegmentKind.Aside)
                    kinds[i] = SegmentKind.Aside;
            }

            // On a box page, which blocks belong to the box and which to the chapter text around it.
            var inBox = boxTitle is null ? null : BoxMembership(ordered, kinds);
            bool? currentInBox = null;

            for (var i = 0; i < ordered.Count; i++)
            {
                var b = ordered[i];
                if (inBox is not null && inBox[i] != currentInBox)
                {
                    // Stream switch: box title, or empty text = back to the chapter's main text.
                    segments.Add(new LayoutSegment(SegmentKind.BoxMarker, inBox[i] ? boxTitle! : "", page.Number));
                    currentInBox = inBox[i];
                }

                switch (kinds[i])
                {
                    case SegmentKind.Heading when options.Structured:
                        var headingText = JoinLines(b.Lines, options);
                        var level = HeadingLevel(b.FontSize, SizeOf(page.BodyFont));
                        // A heading wrapped into two blocks: "Relationship between" + "government debt and interest rates".
                        if (char.IsLower(headingText[0]) && segments.Count > 0 && segments[^1] is { Kind: SegmentKind.Heading } prev
                            && prev.Page == page.Number && prev.HeadingLevel == level)
                        {
                            segments[^1] = prev with { Text = $"{prev.Text} {headingText}" };
                            break;
                        }
                        segments.Add(new LayoutSegment(SegmentKind.Heading, headingText, page.Number, level));
                        headings++;
                        break;
                    case SegmentKind.Heading:
                        segments.Add(new LayoutSegment(SegmentKind.Heading, Clean(b.Lines[0].Text, options), page.Number, 1));
                        headings++;
                        break;
                    case SegmentKind.Aside:
                        var asideText = b.KeyValue
                            ? string.Join("; ", b.Lines.Select(l => Clean(l.Text, options).Replace(" : ", ": "))) + "."
                            : JoinLines(b.Lines, options);
                        segments.Add(new LayoutSegment(SegmentKind.Aside, asideText, page.Number));
                        asides++;
                        break;
                    default:
                        headings += EmitBody(b, page.Number, options, segments);
                        break;
                }
            }

            foreach (var table in pageTables)
                segments.Add(new LayoutSegment(SegmentKind.Table, table, page.Number));
        }

        return new DocumentLayout(segments,
            new LayoutStats(pages.Count, blocks, droppedMargin, droppedOffPage, headings, asides, fragments, skipped, tables, markers));
    }

    // ---------- page reading ----------

    private static PageData ReadPage(Page page)
    {
        // Some PDFs draw each letter twice to fake bold ("RERPE P ORO R T"); remove the duplicates.
        var letters = DuplicateOverlappingTextProcessor.Get(page.Letters);
        var words = NearestNeighbourWordExtractor.Instance.GetWords(letters).ToList();
        var rawBlocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);

        var blocks = new List<PageBlock>();
        var offPage = 0;
        foreach (var rb in rawBlocks)
        {
            var box = rb.BoundingBox;
            // Content outside the visible page (e.g. the other half of a two-page spread).
            if (box.Right <= 0 || box.Left >= page.Width || box.Top <= 0 || box.Bottom >= page.Height)
            {
                offPage++;
                continue;
            }

            var lines = rb.TextLines.Select(l => new Line(l.Words.Select(w => new LineWord(
                w.Text, FontName(w), w.Letters.Count > 0 ? w.Letters[0].PointSize : 0)).ToList(), l.BoundingBox.Bottom)).ToList();
            if (lines.All(l => string.IsNullOrWhiteSpace(l.Text))) continue;

            var letterStats = rb.TextLines.SelectMany(l => l.Words).SelectMany(w => w.Letters).ToList();
            var font = letterStats.GroupBy(l => FontKey(l.FontName, l.PointSize))
                .OrderByDescending(g => g.Count()).First().Key;
            var size = letterStats.Average(l => l.PointSize);
            var text = string.Join(" ", lines.Select(l => l.Text));
            var inMargin = box.Bottom < BottomMargin || box.Top > page.Height - TopMargin;

            blocks.Add(new PageBlock(lines, box.Left, box.Right, box.Top, box.Bottom, font, size, inMargin,
                MarginKeyOf(text), PageNumberRegex().IsMatch(text.Trim())));
        }

        // Body font = the font/size carrying the most letters on this page.
        var bodyFont = blocks
            .SelectMany(b => b.Lines.SelectMany(l => l.Words).Select(w => (Key: FontKey(w.Font, w.Size), w.Text.Length)))
            .GroupBy(x => x.Key)
            .OrderByDescending(g => g.Sum(x => x.Length))
            .Select(g => g.Key)
            .FirstOrDefault() ?? "";

        return new PageData(page.Number, page.Width, blocks, bodyFont, offPage);
    }

    /// <summary>
    /// Two-column reading order. Blocks that cross the middle gutter (titles, full-width bands)
    /// split the page into horizontal bands; inside each band read the left column, then the right.
    /// </summary>
    private static List<PageBlock> ColumnOrder(List<PageBlock> blocks, double pageWidth)
    {
        var mid = pageWidth / 2;
        bool Spans(PageBlock b) => b.Left < mid - 15 && b.Right > mid + 15;

        var spanning = blocks.Where(Spans).OrderByDescending(b => b.Top).ToList();
        var columns = blocks.Where(b => !Spans(b)).ToList();
        var result = new List<PageBlock>();
        var upper = double.MaxValue;

        void EmitBand(double lowerExclusive)
        {
            var band = columns.Where(b => b.CenterY <= upper && b.CenterY > lowerExclusive).ToList();
            result.AddRange(band.Where(b => b.CenterX < mid).OrderByDescending(b => b.Top));
            result.AddRange(band.Where(b => b.CenterX >= mid).OrderByDescending(b => b.Top));
        }

        foreach (var s in spanning)
        {
            EmitBand(s.CenterY);
            result.Add(s);
            upper = s.CenterY;
        }
        EmitBand(double.MinValue);
        return result;
    }

    private static List<PageBlock> TopDown(List<PageBlock> blocks) =>
        blocks.OrderByDescending(b => b.Top).ThenBy(b => b.Left).ToList();

    /// <summary>
    /// Stat panels and simple tables are often laid out as a column of labels next to a column of
    /// values. Read separately, "Passports produced" ends up far from "15,901,600". Pair each
    /// value line with the label lines at the same height: "Passports produced: 15,901,600".
    /// </summary>
    private static List<PageBlock> PairLabelsWithValues(List<PageBlock> blocks)
    {
        var result = blocks.ToList();
        foreach (var values in blocks.Where(IsValueColumn))
        {
            var labels = blocks
                .Where(a => a != values && result.Contains(a) && a.Right < values.Left && !IsValueColumn(a)
                    && Overlap(a, values) > 0.6 && a.Lines.Count >= values.Lines.Count)
                .OrderBy(a => values.Left - a.Right)
                .FirstOrDefault();
            if (labels is null) continue;

            var rows = values.Lines.Select(v => (Value: v, Labels: new List<Line>())).ToList();
            foreach (var l in labels.Lines)
                rows.MinBy(r => Math.Abs(r.Value.Y - l.Y)).Labels.Add(l);
            if (rows.Any(r => r.Labels.Count == 0)) continue;   // not aligned: leave as is

            var lines = rows.Select(r => new Line(
                r.Labels.SelectMany(l => l.Words).Append(new LineWord(":", "", 0)).Concat(r.Value.Words).ToList(),
                r.Value.Y)).ToList();
            var merged = labels with
            {
                Lines = lines,
                Right = values.Right,
                Top = Math.Max(labels.Top, values.Top),
                Bottom = Math.Min(labels.Bottom, values.Bottom),
                KeyValue = true,
            };
            result.Remove(labels);
            result.Remove(values);
            result.Add(merged);
        }
        return result;

        static bool IsValueColumn(PageBlock b) =>
            b.Lines.Count >= 2 && b.Lines.Count(l => l.Text.Any(char.IsDigit) && l.Words.Count <= 4) >= b.Lines.Count * 0.8;

        static double Overlap(PageBlock a, PageBlock b)
        {
            var inter = Math.Min(a.Top, b.Top) - Math.Max(a.Bottom, b.Bottom);
            return inter <= 0 ? 0 : inter / Math.Min(a.Top - a.Bottom, b.Top - b.Bottom);
        }
    }

    /// <summary>
    /// Long-report headings: short blocks (up to 3 lines, no closing period) set in a different font family
    /// or weight than the body, at body size or larger. "South Asia" (Helvetica Bold 16) and
    /// "Recent developments" (Helvetica 11) both qualify; captions such as "TABLE 2.8 …" do not.
    /// </summary>
    private static SegmentKind ClassifyStructured(PageBlock b, string bodyFont)
    {
        var bodySize = SizeOf(bodyFont);
        var text = string.Join(" ", b.Lines.Select(l => l.Text)).Trim();
        var words = b.Lines.Sum(l => l.Words.Count);
        var differentStyle = FontFamily(FontOf(b.Font)) != FontFamily(FontOf(bodyFont)) || IsHeadingFont(FontOf(b.Font));

        if (b.Lines.Count <= 3 && words <= 20 && differentStyle && b.FontSize >= bodySize * 0.95
            && !text.EndsWith('.') && text.Any(char.IsLetter) && !LooksLikeChartOrTableText(text))
            return SegmentKind.Heading;

        if (b.Font == bodyFont) return SegmentKind.Body;
        var sameFamily = FontFamily(FontOf(b.Font)) == FontFamily(FontOf(bodyFont));
        if (sameFamily && Math.Abs(b.FontSize - bodySize) <= bodySize * 0.1 && !IsHeadingFont(FontOf(b.Font)))
            return SegmentKind.Body;
        return SegmentKind.Aside;
    }

    /// <summary>
    /// Superscript footnote references are tiny numbers next to normal text ("electricity ² sourced").
    /// Marking them as [^2] keeps the link to endnote 2 instead of leaving a stray "2" in the sentence.
    /// </summary>
    private static PageBlock MarkFootnotes(PageBlock b, double bodySize, ref int count)
    {
        // Compare with the larger of the surrounding text and the page body: an endnote number often sits alone in its block.
        var largest = Math.Max(bodySize, b.Lines.SelectMany(l => l.Words).Select(w => w.Size).DefaultIfEmpty(0).Max());
        var found = 0;
        var lines = b.Lines.Select(l => l with
        {
            Words = l.Words.Select(w =>
            {
                if (w.Size > largest * 0.75 || !FootnoteNumberRegex().IsMatch(w.Text)) return w;
                found++;
                return w with { Text = $"[^{w.Text}]" };
            }).ToList(),
        }).ToList();
        count += found;
        return found == 0 ? b : b with { Lines = lines.Select(l => new Line(l.Words, l.Y)).ToList() };
    }

    /// <summary>
    /// Tables drawn as separate text cells ("• Production" … "50%") come out as scattered fragments.
    /// Cells whose centres line up in rows, and whose left edges line up in columns, are rebuilt as a
    /// Markdown table so the label and its value stay together. Rows must mostly contain numbers, which
    /// keeps chart labels ("Transportation  Transportation  Transportation") out.
    /// </summary>
    private static (List<PageBlock> Remaining, List<string> Tables) ExtractTables(List<PageBlock> blocks, LayoutOptions options)
    {
        var cells = blocks.Where(b => b.Lines.Count <= 2 && b.Lines.Sum(l => l.Words.Count) <= 12 && !b.InMargin).ToList();

        // Rows: cells whose vertical centres are within 3 pt.
        var rows = new List<List<PageBlock>>();
        foreach (var c in cells.OrderByDescending(c => c.CenterY))
        {
            if (rows.Count > 0 && Math.Abs(rows[^1][0].CenterY - c.CenterY) <= 3) rows[^1].Add(c);
            else rows.Add([c]);
        }
        rows = rows.Where(r => r.Count >= 2).ToList();

        // Runs of rows close together vertically form one table candidate.
        var runs = new List<List<List<PageBlock>>>();
        foreach (var r in rows)
        {
            if (runs.Count > 0 && runs[^1][^1][0].CenterY - r[0].CenterY <= 24) runs[^1].Add(r);
            else runs.Add([r]);
        }

        var used = new HashSet<PageBlock>();
        var tables = new List<string>();
        foreach (var run in runs.Where(r => r.Count >= 3))
        {
            // Columns: left edges that recur in at least half of the rows.
            var anchors = new List<double>();
            foreach (var left in run.SelectMany(r => r.Select(c => c.Left)).Order())
                if (anchors.Count == 0 || left - anchors[^1] > 12) anchors.Add(left);
            var columns = anchors
                .Where(a => run.Count(r => r.Any(c => Math.Abs(c.Left - a) <= 12)) >= run.Count / 2.0)
                .ToList();
            var numericRows = run.Count(r => r.Any(c => c.Lines.Any(l => l.Text.Any(char.IsDigit))));
            if (columns.Count < 2 || numericRows < run.Count * 0.6) continue;

            var md = new System.Text.StringBuilder();
            for (var i = 0; i < run.Count; i++)
            {
                var cellsByColumn = columns.Select(col => string.Join(" ", run[i]
                    .Where(c => columns.MinBy(a => Math.Abs(a - c.Left)) == col)
                    .OrderBy(c => c.Left)
                    .Select(c => Clean(JoinLines(c.Lines, options), options).Replace("|", "/")))).ToList();
                md.Append("| ").Append(string.Join(" | ", cellsByColumn)).Append(" |\n");
                if (i == 0) md.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", columns.Count))).Append('\n');
                foreach (var c in run[i]) used.Add(c);
            }
            tables.Add(md.ToString().TrimEnd());
        }

        return (blocks.Where(b => !used.Contains(b)).ToList(), tables);
    }

    /// <summary>
    /// Box text is often set slightly smaller than chapter text (10.6 vs 11 pt). When a box page holds both
    /// sizes, the smaller one is the box. Headings and captions join the stream of the next body block.
    /// </summary>
    private static bool[] BoxMembership(List<PageBlock> ordered, List<SegmentKind> kinds)
    {
        var bodySizes = ordered.Where((b, i) => kinds[i] == SegmentKind.Body).Select(b => b.FontSize).ToList();
        var result = new bool[ordered.Count];
        if (bodySizes.Count == 0 || bodySizes.Max() - bodySizes.Min() < 0.3)
        {
            Array.Fill(result, true);
            return result;
        }

        var threshold = bodySizes.Max() - 0.2;
        bool? next = null;
        for (var i = ordered.Count - 1; i >= 0; i--)
        {
            if (kinds[i] == SegmentKind.Body) next = ordered[i].FontSize < threshold;
            result[i] = next ?? true;
        }
        return result;
    }

    /// <summary>1 = title page (chapter titles come from bookmarks instead), 2 = section/region, 3 = sub-section.</summary>
    private static int HeadingLevel(double size, double bodySize) =>
        size >= bodySize * 1.7 ? 1 : size >= bodySize * 1.3 ? 2 : 3;

    private static bool IsNumericFragment(PageBlock b, double bodySize)
    {
        if (b.Lines.All(l => l.Words.All(w => w.Text.StartsWith("[^")))) return false;   // footnote marker, not chart noise
        if (b.FontSize < 6.5) return true;
        var tokens = b.Lines.SelectMany(l => l.Words).Select(w => w.Text).ToList();
        var numeric = tokens.Count(t => NumericTokenRegex().IsMatch(t));
        return b.FontSize < bodySize * 0.85 && numeric >= tokens.Count * 0.6;
    }

    /// <summary>
    /// Bold text inside tables and charts that would otherwise pass as a heading: captions ("1 TABLE 2.6 …"),
    /// panel titles ("E. Illustrative …"), column headers ("2027f 0.1 0.1"), decorative all-caps titles.
    /// </summary>
    private static bool LooksLikeChartOrTableText(string text)
    {
        if (CaptionRegex().IsMatch(text) || EmbeddedCaptionRegex().IsMatch(text) || PanelLabelRegex().IsMatch(text)) return true;
        if (char.IsDigit(text[0]) || text.StartsWith("Memo", StringComparison.OrdinalIgnoreCase)) return true;
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Count(t => NumericTokenRegex().IsMatch(t) || ForecastYearRegex().IsMatch(t)) >= Math.Max(1, tokens.Length * 0.3)) return true;
        var letters = text.Where(char.IsLetter).ToList();
        return tokens.Length >= 2 && letters.All(char.IsUpper);
    }

    /// <summary>"AGaramondPro-Regular" → "agaramondpro", "HelveticaLTStd-Bold" → "helveticaltstd".</summary>
    private static string FontFamily(string font) => font.Split('-', ',')[0].ToLowerInvariant();

    private static SegmentKind Classify(PageBlock b, string bodyFont, bool headingsFromFonts)
    {
        if (b.KeyValue) return SegmentKind.Aside;
        var bodySize = SizeOf(bodyFont);
        if (headingsFromFonts && b.Lines.Count == 1)
        {
            var text = b.Lines[0].Text.Trim();
            var wordCount = b.Lines[0].Words.Count;
            var styled = IsHeadingFont(FontOf(b.Font)) || b.FontSize >= bodySize * 1.15;
            if (styled && wordCount <= 14 && !text.EndsWith('.') && text.Any(char.IsLetter) && b.Font != bodyFont)
                return SegmentKind.Heading;
        }

        if (b.Font == bodyFont) return SegmentKind.Body;
        // Same size, regular weight, just a different font subset name: still body text.
        var sameFamily = FontOf(b.Font).Split('-')[0] == FontOf(bodyFont).Split('-')[0];
        if (sameFamily && Math.Abs(b.FontSize - bodySize) <= bodySize * 0.1 && !IsHeadingFont(FontOf(b.Font)))
            return SegmentKind.Body;
        return SegmentKind.Aside;
    }

    /// <summary>
    /// Emits body text, splitting out run-in headings: bold words that open a paragraph
    /// ("Strategic Vision GPO continues to ..."). Returns the number of headings found.
    /// </summary>
    private int EmitBody(PageBlock b, int page, LayoutOptions options, List<LayoutSegment> segments)
    {
        var headings = 0;
        var buffer = new List<Line>();
        var paragraphStart = true;

        foreach (var line in b.Lines)
        {
            if (options.HeadingsFromFonts && paragraphStart)
            {
                var run = line.Words.TakeWhile(w => IsHeadingFont(w.Font)).ToList();
                var rest = line.Words.Skip(run.Count).ToList();
                // Title-like: starts with a capital, does not trail off with a comma (that is a styled callout line).
                if (run.Count is > 0 and <= 8 && rest.Count > 0 && char.IsUpper(run[0].Text[0]) && !run[^1].Text.EndsWith(','))
                {
                    if (buffer.Count > 0) segments.Add(new LayoutSegment(SegmentKind.Body, JoinLines(buffer, options), page));
                    buffer.Clear();
                    // Long reports: bold lead-ins sit below the real section levels.
                    segments.Add(new LayoutSegment(SegmentKind.Heading,
                        Clean(string.Join(" ", run.Select(w => w.Text)), options), page, options.Structured ? 4 : 2));
                    headings++;
                    buffer.Add(new Line(rest, line.Y));
                    paragraphStart = EndsSentence(line.Text);
                    continue;
                }
            }

            buffer.Add(line);
            paragraphStart = EndsSentence(line.Text);
        }

        if (buffer.Count > 0) segments.Add(new LayoutSegment(SegmentKind.Body, JoinLines(buffer, options), page));
        return headings;
    }

    // ---------- text helpers ----------

    /// <summary>Joins wrapped lines, re-joining words hyphenated across a line break ("deposi-" + "tory").</summary>
    private static string JoinLines(IReadOnlyList<Line> lines, LayoutOptions options)
    {
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            var text = line.Text.Trim();
            if (text.Length == 0) continue;
            if (sb.Length > 0)
            {
                var hyphen = options.RepairText && sb.Length > 1 && sb[^1] == '-' && char.IsLetter(sb[^2]);
                if (hyphen && char.IsLower(text[0])) sb.Length--;       // "deposi-" + "tory" → "depository"
                else if (!hyphen) sb.Append(' ');                         // "Vance-" + "Cooks" → "Vance-Cooks"
            }
            sb.Append(text);
        }
        return Clean(sb.ToString(), options);
    }

    private static string Clean(string text, LayoutOptions options) =>
        options.RepairText ? TextCleaner.Clean(text) : TextCleaner.CollapseWhitespace(text);

    private static bool EndsSentence(string text)
    {
        var t = text.TrimEnd().TrimEnd('"', '”', '’', ')');
        return t.EndsWith('.') || t.EndsWith('!') || t.EndsWith('?') || t.EndsWith(':');
    }

    private static readonly string[] HeadingWeights = ["bold", "-bd", "-md", "medium", "semibold", "demi", "heavy", "black"];

    private static bool IsHeadingFont(string fontName)
    {
        var f = fontName.ToLowerInvariant();
        return HeadingWeights.Any(f.Contains);
    }

    private static string FontName(UglyToad.PdfPig.Content.Word w) => w.Letters.Count > 0 ? StripSubset(w.Letters[0].FontName) : "";

    /// <summary>"DQGWOV+ITCGaramondStd-Lt" → "ITCGaramondStd-Lt".</summary>
    private static string StripSubset(string? font) => font is null ? "" : font[(font.IndexOf('+') + 1)..];

    private static string FontKey(string? font, double size) => $"{StripSubset(font)}|{Math.Round(size)}";
    private static string FontOf(string key) => key.Split('|')[0];
    private static double SizeOf(string key) => key.Contains('|') ? double.Parse(key.Split('|')[1]) : 10;

    private static string MarginKeyOf(string text) =>
        NonLetterRegex().Replace(text.ToLowerInvariant(), "");

    [GeneratedRegex(@"^(page\s*)?(\d{1,3}|[ivxlc]{1,6})$", RegexOptions.IgnoreCase)]
    private static partial Regex PageNumberRegex();

    [GeneratedRegex(@"[^a-z]")]
    private static partial Regex NonLetterRegex();

    [GeneratedRegex(@"^\d{1,2}$")]
    private static partial Regex FootnoteNumberRegex();

    [GeneratedRegex(@"^BOX\s+\d+(\.\d+)*\b")]
    private static partial Regex BoxRegex();

    [GeneratedRegex(@"\s*\(continued\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ContinuedRegex();

    [GeneratedRegex(@"^(TABLE|FIGURE|BOX|Note|Notes|Source|Sources)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CaptionRegex();

    [GeneratedRegex(@"^[-–+$]?[\d.,%/]+[a-z]?$|^\.\.$")]
    private static partial Regex NumericTokenRegex();

    [GeneratedRegex(@"\b(TABLE|FIGURE)\s+[A-Z]?\d")]
    private static partial Regex EmbeddedCaptionRegex();

    [GeneratedRegex(@"^[A-Z]\.\s")]
    private static partial Regex PanelLabelRegex();

    [GeneratedRegex(@"^20\d\d[ef]?$|^20\d\d/\d\d[ef]?$")]
    private static partial Regex ForecastYearRegex();

    // ---------- internal types ----------

    private sealed record LineWord(string Text, string Font, double Size);

    private sealed record Line(IReadOnlyList<LineWord> Words, double Y)
    {
        public string Text { get; } = string.Join(" ", Words.Select(w => w.Text));
    }

    private sealed record PageBlock(
        IReadOnlyList<Line> Lines, double Left, double Right, double Top, double Bottom,
        string Font, double FontSize, bool InMargin, string MarginKey, bool IsPageNumber, bool KeyValue = false)
    {
        public double CenterX => (Left + Right) / 2;
        public double CenterY => (Top + Bottom) / 2;
    }

    private sealed record PageData(int Number, double Width, List<PageBlock> Blocks, string BodyFont, int OffPageCount);
}
