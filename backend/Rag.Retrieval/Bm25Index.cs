using System.Text.RegularExpressions;

namespace Rag.Retrieval;

/// <summary>
/// Okapi BM25 keyword index. Catches what embeddings are weak at: exact terms,
/// acronyms ("FDsys") and numbers ("802,834").
/// </summary>
public sealed partial class Bm25Index
{
    private const double K1 = 1.2;
    private const double B = 0.75;

    private readonly List<Dictionary<string, int>> _termFreqs = [];
    private readonly List<int> _lengths = [];
    private readonly Dictionary<string, int> _docFreq = [];
    private double _avgLength;

    public Bm25Index(IEnumerable<string> documents)
    {
        foreach (var doc in documents)
        {
            var tf = new Dictionary<string, int>();
            var tokens = Tokenize(doc);
            foreach (var t in tokens) tf[t] = tf.GetValueOrDefault(t) + 1;
            foreach (var t in tf.Keys) _docFreq[t] = _docFreq.GetValueOrDefault(t) + 1;
            _termFreqs.Add(tf);
            _lengths.Add(tokens.Count);
        }
        _avgLength = _lengths.Count == 0 ? 0 : _lengths.Average();
    }

    /// <summary>Returns (document index, score) pairs, best first.</summary>
    public List<(int Index, double Score)> Search(string query, int top, Func<int, bool>? allow = null)
    {
        var terms = Tokenize(query).Distinct().ToList();
        var n = _termFreqs.Count;
        var scores = new List<(int, double)>();

        for (var i = 0; i < n; i++)
        {
            if (allow is not null && !allow(i)) continue;
            double score = 0;
            foreach (var term in terms)
            {
                if (!_termFreqs[i].TryGetValue(term, out var f)) continue;
                var df = _docFreq[term];
                var idf = Math.Log(1 + (n - df + 0.5) / (df + 0.5));
                score += idf * f * (K1 + 1) / (f + K1 * (1 - B + B * _lengths[i] / _avgLength));
            }
            if (score > 0) scores.Add((i, score));
        }

        return scores.OrderByDescending(s => s.Item2).Take(top).ToList();
    }

    public static List<string> Tokenize(string text)
    {
        // "802,834" → "802834" so numbers match however they are written.
        var normalized = ThousandsRegex().Replace(text.ToLowerInvariant(), "");
        return TokenRegex().Matches(normalized)
            .Select(m => Stem(m.Value))
            .Where(t => !StopWords.Contains(t))
            .ToList();
    }

    /// <summary>Minimal stemming: plural forms only ("libraries" → "library", "visits" → "visit").</summary>
    private static string Stem(string t)
    {
        if (t.Length > 4 && t.EndsWith("ies")) return t[..^3] + "y";
        if (t.Length > 3 && t.EndsWith('s') && !t.EndsWith("ss") && !t.EndsWith("us") && !char.IsDigit(t[^2])) return t[..^1];
        return t;
    }

    private static readonly HashSet<string> StopWords =
    [
        "a", "an", "and", "are", "as", "at", "be", "by", "did", "do", "does", "for", "from", "had", "has", "have",
        "how", "in", "is", "it", "its", "of", "on", "or", "that", "the", "their", "this", "to", "was", "were",
        "what", "when", "where", "which", "who", "why", "will", "with", "many", "much",
    ];

    [GeneratedRegex(@"(?<=\d),(?=\d{3})")]
    private static partial Regex ThousandsRegex();

    [GeneratedRegex(@"[a-z0-9]+(?:\.[0-9]+)?")]
    private static partial Regex TokenRegex();
}
