using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Rag.Core;
using Rag.Generation;
using Rag.Retrieval;

namespace Rag.Hosting;

public sealed class EvalSet
{
    public string Document { get; set; } = "";
    public List<EvalQuestion> Questions { get; set; } = [];
}

public sealed class EvalQuestion
{
    public string Id { get; set; } = "";
    public string Question { get; set; } = "";
    public string Category { get; set; } = "";
    /// <summary>The three questions from the exercise brief: the pass/fail bar.</summary>
    public bool Required { get; set; }
    /// <summary>The answer is not in the document; the only correct reply is a refusal.</summary>
    public bool Trap { get; set; }
    public string? Expected { get; set; }
    /// <summary>Every group must match; within a group any alternative may match ("$375 million" | "$375.7 million").</summary>
    public List<List<string>> MustContain { get; set; } = [];
    /// <summary>Text that identifies a passage holding the answer, used to score retrieval rank.</summary>
    public List<string> Evidence { get; set; } = [];
    /// <summary>Mix-up check: the answer fails if it contains any of these (e.g. another region's figure).</summary>
    public List<string> MustNotContain { get; set; } = [];
    /// <summary>The top-ranked passage must come from a section whose path contains this ("South Asia", "Chapter 3").</summary>
    public string? ExpectedSection { get; set; }
}

/// <param name="SectionHit">Share of questions whose top passage came from the expected section (null when the set has none).</param>
public sealed record LadderStep(string Name, string Description, double Mrr, double HitAt5, IReadOnlyList<int?> Ranks, double? SectionHit = null);

public sealed record AnswerCheck(
    string Id, string Question, string Category, bool Required, bool Trap, string? Expected,
    string Answer, bool Refused, bool Passed, int? Rank, IReadOnlyList<int> CitedPages, string? Error, string? FailReason = null, string? TopSection = null);

public sealed record EvalReport(
    int DocumentId, string Document, DateTime CreatedAt, string ChatModel, string EmbeddingModel, string VectorSearch,
    IReadOnlyList<LadderStep> Ladder, IReadOnlyList<AnswerCheck> Answers,
    int Passed, int Total, bool RequiredPassed, long ElapsedMs);

/// <summary>
/// Measures each technique's contribution. Retrieval ladder: add one technique per row and
/// measure MRR (where the answer passage ranks). Answers: does the final pipeline give the exact
/// fact, and refuse when the document does not contain it?
/// </summary>
public sealed partial class EvaluationService(
    IDocumentRepository documents,
    IEvalRepository evalRuns,
    RetrievalPipeline retrieval,
    RagService rag,
    AnswerGenerator generator,
    EmbeddingService embeddings,
    IDenseSearch denseSearch)
{
    public static string EvalSetPath(RagDocument doc) =>
        AppPaths.Resolve(Path.Combine("eval", Path.GetFileNameWithoutExtension(doc.FilePath) + ".json"));

    public async Task<EvalSet> LoadSetAsync(int documentId, CancellationToken ct = default)
    {
        var doc = await documents.GetAsync(documentId, ct) ?? throw new KeyNotFoundException($"Document {documentId} not found");
        var path = EvalSetPath(doc);
        if (!File.Exists(path)) throw new FileNotFoundException($"No evaluation set at {path}");
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<EvalSet>(stream, RagService.Json, ct) ?? new EvalSet();
    }

    /// <param name="resume">Keep answers from the latest run that succeeded; only ask questions that failed with an
    /// API error (rate limit, outage). Saves free-tier quota.</param>
    public async Task<EvalReport> RunAsync(int documentId, bool includeAnswers, IProgress<string>? progress = null,
        CancellationToken ct = default, bool resume = false)
    {
        var sw = Stopwatch.StartNew();
        var doc = await documents.GetAsync(documentId, ct) ?? throw new KeyNotFoundException($"Document {documentId} not found");
        var set = await LoadSetAsync(documentId, ct);
        var answerable = set.Questions.Where(q => !q.Trap && q.Evidence.Count > 0).ToList();

        // ---- Retrieval ladder: one technique added per step (no LLM calls, so it is free and fast)
        var ladder = new List<LadderStep>();
        var withSection = answerable.Where(q => q.ExpectedSection is not null).ToList();
        foreach (var (name, description, options) in LadderSteps(doc.Profile))
        {
            progress?.Report($"Retrieval: {name}");
            var ranks = new List<int?>();
            var sectionHits = 0;
            foreach (var q in answerable)
            {
                var result = await retrieval.RetrieveAsync(documentId, q.Question, options, ct);
                ranks.Add(RankOf(result.Ranked, q.Evidence));
                if (q.ExpectedSection is { } expected && result.Ranked.Count > 0
                    && result.Ranked[0].Chunk.SectionPath.Contains(expected, StringComparison.OrdinalIgnoreCase))
                    sectionHits++;
            }
            ladder.Add(new LadderStep(name, description,
                Math.Round(ranks.Average(r => r is null ? 0 : 1.0 / r.Value), 3),
                Math.Round(ranks.Count(r => r <= 5) / (double)ranks.Count, 3),
                ranks,
                withSection.Count == 0 ? null : Math.Round(sectionHits / (double)withSection.Count, 3)));
        }

        // ---- Answers with the full pipeline
        var answers = new List<AnswerCheck>();
        if (includeAnswers)
        {
            var previous = new Dictionary<string, AnswerCheck>();
            if (resume && await evalRuns.LatestAsync(documentId, ct) is { } last)
            {
                var lastReport = JsonSerializer.Deserialize<EvalReport>(last.ResultJson, RagService.Json);
                foreach (var a in lastReport?.Answers.Where(a => a.Error is null) ?? []) previous[a.Id] = a;
            }

            foreach (var q in set.Questions)
            {
                if (previous.TryGetValue(q.Id, out var kept) && kept.Question == q.Question)
                {
                    // Re-grade the stored answer against the current criteria: fixing a test costs no LLM call.
                    var reason = Grade(q, kept.Answer, kept.Refused, kept.TopSection);
                    answers.Add(kept with { Expected = q.Expected, Passed = reason is null, FailReason = reason });
                    continue;
                }
                progress?.Report($"Answering: {q.Question}");
                answers.Add(await CheckAnswerAsync(documentId, q, ct));
            }
        }

        var report = new EvalReport(documentId, doc.Name, DateTime.UtcNow, generator.ModelName, embeddings.ModelName, denseSearch.Name,
            ladder, answers, answers.Count(a => a.Passed), answers.Count,
            answers.Count > 0 && answers.Where(a => a.Required).All(a => a.Passed), sw.ElapsedMilliseconds);

        await evalRuns.AddAsync(documentId, JsonSerializer.Serialize(report, RagService.Json), ct);
        return report;
    }

    private async Task<AnswerCheck> CheckAnswerAsync(int documentId, EvalQuestion q, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var a = await rag.AskAsync(documentId, q.Question, saveHistory: false, ct: ct);
                var topSection = a.Citations.FirstOrDefault()?.Section;
                var reason = Grade(q, a.Answer, a.Refused, topSection);

                var rank = q.Evidence.Count > 0 ? RankOf(a.Citations.Select(c => c.Text).ToList(), q.Evidence) : null;
                var cited = a.Citations.Where(c => c.Cited).Select(c => c.Page).Distinct().ToList();
                return new AnswerCheck(q.Id, q.Question, q.Category, q.Required, q.Trap, q.Expected,
                    a.Answer, a.Refused, reason is null, rank, cited, null, reason, topSection);
            }
            catch (Exception ex) when (attempt < 4 && IsRateLimit(ex))
            {
                // Free tiers allow a limited number of requests per minute: wait and retry.
                await Task.Delay(TimeSpan.FromSeconds(15 * attempt), ct);
            }
            catch (Exception ex)
            {
                return new AnswerCheck(q.Id, q.Question, q.Category, q.Required, q.Trap, q.Expected,
                    "", false, false, null, [], ex.Message);
            }
        }
    }

    /// <summary>Why an answer fails the question's criteria, or null when it passes.</summary>
    private static string? Grade(EvalQuestion q, string answer, bool refused, string? topSection)
    {
        if (q.Trap) return refused ? null : "Should have refused";
        if (refused) return "Refused an answerable question";

        var missing = q.MustContain.Where(group => !group.Any(alt => Contains(answer, alt))).Select(g => g[0]).ToList();
        if (missing.Count > 0) return $"Missing: {string.Join(", ", missing)}";

        var mixedUp = q.MustNotContain.Where(x => Contains(answer, x)).ToList();
        if (mixedUp.Count > 0) return $"Mixed up with another section: {string.Join(", ", mixedUp)}";

        var wrongSection = q.ExpectedSection is not null && topSection is not null
            && !topSection.Contains(q.ExpectedSection, StringComparison.OrdinalIgnoreCase);
        return wrongSection ? $"Top passage from the wrong section ({topSection})" : null;
    }

    /// <summary>One technique added per step, so each row shows what that technique contributes.</summary>
    private static IEnumerable<(string Name, string Description, PipelineOptions Options)> LadderSteps(DocumentProfile profile)
    {
        var full = PipelineOptions.ForProfile(profile);
        yield return ("Naive", "1,000-character chunks in raw PDF order, dense search", PipelineOptions.Naive);

        if (profile == DocumentProfile.LongReport)
        {
            var structured = full with { Hybrid = false, Rerank = false, Query = QueryMode.None, MetadataFilter = false, SmallToBig = false };
            yield return ("+ Structure-aware chunks", "Sections from bookmarks and fonts, parent/child chunks, heading path embedded; dense search", structured);
            yield return ("+ Hybrid search", "Dense + BM25 merged with reciprocal rank fusion", structured with { Hybrid = true });
            yield return ("+ Cross-encoder rerank", "Top 20 reread together with the question (and its heading path)", structured with { Hybrid = true, Rerank = true });
            yield return ("+ Query routing & filter", "Chapter / region named in the question → search only that section", full);
            yield break;
        }

        yield return profile == DocumentProfile.Visual
            ? ("+ Tables, footnotes & figures", "Tables rebuilt as Markdown, footnotes attached to claims, charts described by vision; dense search",
                full with { Hybrid = false, Rerank = false })
            : ("+ Reading order & sentence chunks", "Column-aware text, whole sentences with overlap, dense search",
                full with { Hybrid = false, Rerank = false });
        yield return ("+ Hybrid search", "Dense + BM25 merged with reciprocal rank fusion", full with { Rerank = false });
        yield return ("+ Cross-encoder rerank", "Top 20 reread together with the question", full);
    }

    private static bool IsRateLimit(Exception ex) =>
        ex.Message.Contains("429") || ex.Message.Contains("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("rate", StringComparison.OrdinalIgnoreCase);

    private static int? RankOf(IReadOnlyList<ScoredChunk> ranked, List<string> evidence) =>
        RankOf(ranked.Select(r => r.Chunk.Text).ToList(), evidence);

    private static int? RankOf(IReadOnlyList<string> texts, List<string> evidence)
    {
        for (var i = 0; i < texts.Count; i++)
            if (evidence.Any(e => Contains(texts[i], e))) return i + 1;
        return null;
    }

    private static bool Contains(string text, string fragment) =>
        Normalize(text).Contains(Normalize(fragment), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string s) => SpaceRegex().Replace(s.Replace('’', '\'').Replace('–', '-'), " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpaceRegex();
}
