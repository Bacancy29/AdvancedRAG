using System.Text;
using System.Text.RegularExpressions;

namespace Rag.Ingestion.Layout;

public static partial class TextCleaner
{
    private static readonly (string From, string To)[] Ligatures =
    [
        ("ﬀ", "ff"), ("ﬁ", "fi"), ("ﬂ", "fl"), ("ﬃ", "ffi"), ("ﬄ", "ffl"),
        ("ﬅ", "st"), ("ﬆ", "st"), ("­", ""), (" ", " "), (" ", " "),
    ];

    public static string Clean(string text)
    {
        var sb = new StringBuilder(text);
        foreach (var (from, to) in Ligatures) sb.Replace(from, to);
        return CollapseWhitespace(sb.ToString());
    }

    public static string CollapseWhitespace(string text) => WhitespaceRegex().Replace(text, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
