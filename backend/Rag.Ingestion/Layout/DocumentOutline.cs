using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Outline;

namespace Rag.Ingestion.Layout;

public sealed record OutlineEntry(string Title, int Page);

/// <summary>
/// Top-level structure from the PDF's bookmarks: which chapter each page belongs to, and which
/// pages are a table of contents. Section, region and sub-section levels come from fonts instead,
/// because bookmarks are often flat or point to the page before the heading.
/// </summary>
public sealed partial class DocumentOutline
{
    private readonly List<OutlineEntry> _chapters;

    private DocumentOutline(List<OutlineEntry> chapters, IReadOnlySet<int> contentsPages, IReadOnlyList<OutlineEntry> all)
    {
        _chapters = chapters;
        ContentsPages = contentsPages;
        All = all;
    }

    public IReadOnlyList<OutlineEntry> Chapters => _chapters;
    public IReadOnlyList<OutlineEntry> All { get; }
    public IReadOnlySet<int> ContentsPages { get; }
    public bool HasBookmarks => All.Count > 0;

    /// <summary>The chapter a page belongs to, e.g. "Chapter 3: A Rising Challenge: Sovereign Debt …".</summary>
    public string ChapterOf(int page) => _chapters.LastOrDefault(c => c.Page <= page)?.Title ?? "Front matter";

    public static DocumentOutline Read(string pdfPath)
    {
        using var pdf = PdfDocument.Open(pdfPath);
        var all = new List<OutlineEntry>();
        if (pdf.TryGetBookmarks(out var bookmarks))
        {
            void Walk(BookmarkNode n)
            {
                if (n is DocumentBookmarkNode d) all.Add(new OutlineEntry(Normalize(n.Title), d.PageNumber));
                foreach (var c in n.Children) Walk(c);
            }
            foreach (var r in bookmarks.Roots) Walk(r);
        }
        all = all.OrderBy(e => e.Page).ToList();

        var chapters = all.Where(e => IsChapterLevel(e.Title)).ToList();

        // Table-of-contents pages mention every chapter and region: skip them so they never outrank real content.
        var contents = new HashSet<int>();
        for (var i = 0; i < all.Count; i++)
        {
            if (!all[i].Title.Contains("Contents", StringComparison.OrdinalIgnoreCase)) continue;
            var end = all.Skip(i + 1).FirstOrDefault(e => !e.Title.Contains("Contents", StringComparison.OrdinalIgnoreCase))?.Page ?? all[i].Page + 1;
            for (var p = all[i].Page; p < end; p++) contents.Add(p);
        }

        return new DocumentOutline(chapters, contents, all);
    }

    /// <summary>Chapters, executive summary, annexes: the level a reader navigates by. Boxes and regions are not.</summary>
    private static bool IsChapterLevel(string title) =>
        ChapterRegex().IsMatch(title) || TopLevelRegex().IsMatch(title);

    /// <summary>"Ch. 3 A Rising Challenge:\nSovereign Debt…" → "Chapter 3: A Rising Challenge: Sovereign Debt…".</summary>
    private static string Normalize(string title)
    {
        var t = Regex.Replace(title.Replace("--", " "), @"\s+", " ").Trim();
        return ChapterPrefixRegex().Replace(t, m => $"Chapter {m.Groups[1].Value}: ");
    }

    [GeneratedRegex(@"^Chapter \d+:")]
    private static partial Regex ChapterRegex();

    [GeneratedRegex(@"^(Executive Summary|Foreword|Statistical Annex|Annex|Appendix|Overview|Introduction)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TopLevelRegex();

    [GeneratedRegex(@"^Ch(?:apter|\.)\s*(\d+)\s*")]
    private static partial Regex ChapterPrefixRegex();
}
