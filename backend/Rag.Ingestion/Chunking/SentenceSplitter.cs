namespace Rag.Ingestion.Chunking;

/// <summary>
/// Splits text into sentences without breaking on abbreviations ("U.S.", "Mr.", "J.") or
/// decimals ("$375.7"). Returns character ranges so callers can map sentences back to pages.
/// </summary>
public static class SentenceSplitter
{
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "u.s", "u.s.c", "u.k", "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "st", "no", "nos", "vol", "inc", "co",
        "corp", "ltd", "dept", "gov", "sen", "rep", "hon", "gen", "e.g", "i.e", "etc", "vs", "al", "approx", "fig",
        "jan", "feb", "mar", "apr", "jun", "jul", "aug", "sep", "sept", "oct", "nov", "dec", "p", "pp", "est",
    };

    private const string Closers = "\"'”’)]";
    private const string Openers = "\"“‘([";

    public static List<(int Start, int End)> Split(string text)
    {
        var result = new List<(int, int)>();
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is not ('.' or '!' or '?')) continue;

            // Include closing quotes/brackets: He said "done." Next...
            var end = i + 1;
            while (end < text.Length && Closers.Contains(text[end])) end++;

            // A boundary needs whitespace and then an uppercase letter, digit or opening quote.
            if (end >= text.Length || !char.IsWhiteSpace(text[end])) continue;
            var next = end;
            while (next < text.Length && char.IsWhiteSpace(text[next])) next++;
            if (next >= text.Length) break;
            var n = text[next];
            if (!(char.IsUpper(n) || char.IsDigit(n) || Openers.Contains(n) || n == '$')) continue;

            if (c == '.' && IsAbbreviation(text, i)) continue;

            result.Add((start, end));
            start = next;
            i = next - 1;
        }

        if (start < text.Length && text[start..].Trim().Length > 0) result.Add((start, text.Length));
        return result;
    }

    private static bool IsAbbreviation(string text, int periodIndex)
    {
        var wordStart = periodIndex;
        while (wordStart > 0 && !char.IsWhiteSpace(text[wordStart - 1]) && !Openers.Contains(text[wordStart - 1])) wordStart--;
        var word = text[wordStart..periodIndex];
        if (word.Length == 0) return false;
        // Single initial: "Donald J. Trump".
        if (word.Length == 1 && char.IsUpper(word[0])) return true;
        return Abbreviations.Contains(word);
    }
}
