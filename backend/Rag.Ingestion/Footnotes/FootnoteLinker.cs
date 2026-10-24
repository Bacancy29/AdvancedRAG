using System.Text.RegularExpressions;
using Rag.Core;

namespace Rag.Ingestion.Footnotes;

/// <summary>
/// Footnotes qualify claims ("…based on U.S. configuration", "…compared with 2015 baseline"). Read alone, the
/// claim can be misleading. The reader marks references as [^7]; this finds the endnote text for each number
/// and attaches it to every chunk that cites it, so the footnote travels with the claim.
/// </summary>
public static partial class FootnoteLinker
{
    /// <summary>Endnote number → text, from the running text of the endnotes pages ("[^1] … [^2] …").</summary>
    public static Dictionary<string, string> ReadEndnotes(string text)
    {
        var notes = new Dictionary<string, string>();

        var markers = MarkerRegex().Matches(text);
        for (var i = 0; i < markers.Count; i++)
        {
            var start = markers[i].Index + markers[i].Length;
            var end = i + 1 < markers.Count ? markers[i + 1].Index : text.Length;
            var note = text[start..end].Trim();
            if (note.Length > 0) notes.TryAdd(markers[i].Groups[1].Value, note);
        }
        return notes;
    }

    /// <summary>Appends "Footnote n: …" to chunks that cite n. Returns how many links were made.</summary>
    public static int Link(List<Chunk> chunks, Dictionary<string, string> notes)
    {
        var links = 0;
        foreach (var c in chunks.Where(c => !IsEndnoteSection(c.SectionPath)))
        {
            var cited = MarkerRegex().Matches(c.Text).Select(m => m.Groups[1].Value).Distinct()
                .Where(notes.ContainsKey).ToList();
            if (cited.Count == 0) continue;
            c.Text += "\n" + string.Join("\n", cited.Select(n => $"Footnote {n}: {notes[n]}"));
            c.TokenCount = Chunking.TokenEstimator.Estimate(c.Text);
            links += cited.Count;
        }
        foreach (var c in chunks.Where(c => IsEndnoteSection(c.SectionPath) && c.ContentType == ContentTypes.Text)) c.ContentType = ContentTypes.Footnote;
        return links;
    }

    public static bool IsEndnoteSection(string sectionPath) => EndnoteSectionRegex().IsMatch(sectionPath);

    [GeneratedRegex(@"\[\^(\d{1,2})\]")]
    private static partial Regex MarkerRegex();

    [GeneratedRegex(@"^(Endnotes|Footnotes|Notes)\b", RegexOptions.IgnoreCase)]
    private static partial Regex EndnoteSectionRegex();
}
