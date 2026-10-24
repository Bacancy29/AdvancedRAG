using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Rag.Core;

namespace Rag.Retrieval;

public sealed class RoutingOptions
{
    /// <summary>How stage 3 routes questions for profiles that use routing: Rules (free, instant) or Llm (one chat call).</summary>
    public QueryMode Mode { get; set; } = QueryMode.Rules;
    /// <summary>Other names for sections: "SAR" → "South Asia". Matching is case-insensitive, whole words.</summary>
    public Dictionary<string, string> Aliases { get; set; } = [];
    /// <summary>Single-word section names too generic to route on ("Outlook", "Risks", "Introduction").</summary>
    public int MinWordsForAutoMatch { get; set; } = 2;
}

/// <param name="Prefixes">Section paths to search inside; empty = search everything.</param>
public sealed record RouteResult(IReadOnlyList<string> Prefixes, string Explanation);

/// <summary>
/// Stage 3, query understanding: turns "What is the growth outlook for South Asia?" into a filter
/// ("Chapter 2: Regional Outlooks > South Asia") so a passage about Sub-Saharan Africa cannot win just
/// because it also talks about growth forecasts.
/// <list type="bullet">
/// <item>Rules: chapter numbers, the executive summary, box numbers, and section names (or their aliases)
/// that appear in the question. Free, instant, predictable.</item>
/// <item>LLM: the chat model picks sections from the document's own outline. Handles paraphrases, costs one call.</item>
/// </list>
/// </summary>
public sealed partial class QueryRouter(RoutingOptions options, IChatClient chat)
{
    public async Task<RouteResult> RouteAsync(string question, IReadOnlyList<string> sectionPaths, QueryMode mode, CancellationToken ct = default)
    {
        var rules = RouteByRules(question, sectionPaths);
        if (mode == QueryMode.Rules || rules.Prefixes.Count > 0) return rules;
        return mode == QueryMode.Llm ? await RouteByLlmAsync(question, sectionPaths, ct) : rules;
    }

    public RouteResult RouteByRules(string question, IReadOnlyList<string> sectionPaths)
    {
        var q = Normalize(question);
        var prefixes = new List<string>();
        var reasons = new List<string>();

        // "Chapter 3", "chapter three", "Ch. 3"
        foreach (Match m in ChapterRegex().Matches(q))
        {
            var n = WordNumbers.TryGetValue(m.Groups[1].Value, out var w) ? w : m.Groups[1].Value;
            var chapter = sectionPaths.Select(TopLevel).FirstOrDefault(p => p.StartsWith($"Chapter {n}:", StringComparison.Ordinal));
            if (chapter is null) continue;
            prefixes.Add(chapter);
            reasons.Add($"\"{m.Value}\" → {chapter}");
        }

        // "Box 3.1"
        foreach (Match m in BoxRegex().Matches(q))
        {
            var box = sectionPaths.Select(p => PrefixThrough(p, $"BOX {m.Groups[1].Value} ")).FirstOrDefault(p => p is not null);
            if (box is null) continue;
            prefixes.Add(box);
            reasons.Add($"\"{m.Value}\" → {box}");
        }

        // Section names written in the question, or their aliases ("SAR" → "South Asia").
        // Only names that point to one place: "Recent developments" exists under every region, so it routes nowhere.
        var names = sectionPaths
            .SelectMany(p => p.Split(" > ").Select((name, depth) => (name, depth, path: p)))
            .Where(x => x.depth > 0 || !x.name.StartsWith("Chapter ", StringComparison.Ordinal))
            .GroupBy(x => x.name)
            .Where(g => g.Select(x => PrefixThrough(x.path, x.name)).Distinct().Count() == 1)
            .Select(g => g.First())
            .ToList();

        foreach (var (name, depth, path) in names)
        {
            var terms = new List<string>();
            if (name.Split(' ').Length >= options.MinWordsForAutoMatch || depth == 0) terms.Add(name);
            terms.AddRange(options.Aliases.Where(a => a.Value.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(a => a.Key));

            var hit = terms.FirstOrDefault(t => Regex.IsMatch(q, $@"\b{Regex.Escape(Normalize(t))}\b"));
            if (hit is null) continue;
            var prefix = PrefixThrough(path, name)!;
            if (prefixes.Any(p => prefix.StartsWith(p, StringComparison.Ordinal))) continue;   // already inside a broader match
            prefixes.Add(prefix);
            reasons.Add($"\"{hit}\" → {prefix}");
        }

        return new RouteResult(prefixes.Distinct().ToList(),
            prefixes.Count == 0 ? "No chapter, section or region named in the question: searching the whole document."
                                : "Routed by rules: " + string.Join("; ", reasons) + ".");
    }

    private async Task<RouteResult> RouteByLlmAsync(string question, IReadOnlyList<string> sectionPaths, CancellationToken ct)
    {
        // Chapters and their direct sections are enough to choose from, and keep the prompt short.
        var outline = sectionPaths
            .Select(p => string.Join(" > ", p.Split(" > ").Take(2)))
            .Distinct().ToList();
        try
        {
            var response = await chat.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System,
                    "You route questions about a report to the sections that answer them. " +
                    "Reply with JSON only: {\"sections\": [\"<exact section path from the list>\"]}. " +
                    "Return an empty list if the question does not name or clearly imply specific sections."),
                new ChatMessage(ChatRole.User, $"Sections:\n{string.Join("\n", outline)}\n\nQuestion: {question}"),
            ], new ChatOptions { Temperature = 0 }, ct);

            var json = JsonObjectRegex().Match(response.Text).Value;
            var picked = JsonDocument.Parse(json).RootElement.GetProperty("sections").EnumerateArray()
                .Select(e => e.GetString() ?? "").Where(outline.Contains).ToList();
            return new RouteResult(picked, picked.Count == 0
                ? "LLM routing: no specific section; searching the whole document."
                : "Routed by LLM: " + string.Join("; ", picked) + ".");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new RouteResult([], $"LLM routing failed ({ex.Message.Split('\n')[0]}); searching the whole document.");
        }
    }

    private static string TopLevel(string path) => path.Split(" > ")[0];

    /// <summary>"Chapter 2: … > South Asia > Outlook" through "South Asia" → "Chapter 2: … > South Asia".</summary>
    private static string? PrefixThrough(string path, string name)
    {
        var parts = path.Split(" > ");
        var i = Array.FindIndex(parts, p => p.Equals(name, StringComparison.Ordinal) || p.StartsWith(name, StringComparison.Ordinal));
        return i < 0 ? null : string.Join(" > ", parts.Take(i + 1));
    }

    private static string Normalize(string s) => Regex.Replace(s.ToLowerInvariant().Replace('’', '\''), @"\s+", " ");

    private static readonly Dictionary<string, string> WordNumbers = new()
    {
        ["one"] = "1", ["two"] = "2", ["three"] = "3", ["four"] = "4", ["five"] = "5", ["six"] = "6",
        ["first"] = "1", ["second"] = "2", ["third"] = "3", ["fourth"] = "4",
    };

    [GeneratedRegex(@"\bch(?:apter|\.)?\s*(\d+|one|two|three|four|five|six)\b")]
    private static partial Regex ChapterRegex();

    [GeneratedRegex(@"\bbox\s+(\d+\.\d+)\b")]
    private static partial Regex BoxRegex();

    [GeneratedRegex(@"\{[\s\S]*\}")]
    private static partial Regex JsonObjectRegex();
}
