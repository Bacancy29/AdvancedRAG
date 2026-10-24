using System.Diagnostics;
using Rag.Core;

namespace Rag.Retrieval;

/// <summary>
/// Online stages 3–5 for one question: Query (routing) → Retrieve (dense + BM25 + RRF, inside the routed
/// sections) → Post-process (rerank, then small-to-big). Every stage records a <see cref="StageTrace"/>
/// so the UI can show exactly what happened.
/// </summary>
public sealed class RetrievalPipeline(
    ChunkIndexCache indexes,
    EmbeddingService embeddings,
    IDenseSearch denseSearch,
    IReranker reranker,
    QueryRouter router,
    RoutingOptions routing,
    RagOptions options)
{
    private const int MinRoutedCandidates = 3;

    public async Task<RetrievalResult> RetrieveAsync(int documentId, string question, PipelineOptions pipeline, CancellationToken ct = default)
    {
        var trace = new List<StageTrace>();
        var index = await indexes.GetAsync(documentId, pipeline.ChunkSet, ct);
        if (index.Chunks.Count == 0) throw new InvalidOperationException("Document has no chunks. Ingest it first.");

        // ---- 3. Query: which chapter / section / region is the question about?
        IReadOnlyList<string>? prefixes = null;
        if (pipeline.Query == QueryMode.None)
        {
            trace.Add(new StageTrace(Stages.Query, "Query understanding", false,
                "Not needed for a single narrative document; question used as-is."));
        }
        else
        {
            var sw0 = Stopwatch.StartNew();
            var sections = index.Chunks.Select(c => c.SectionPath).Where(p => p.Length > 0).Distinct().ToList();
            var mode = routing.Mode == QueryMode.None ? pipeline.Query : routing.Mode;
            var route = await router.RouteAsync(question, sections, mode, ct);
            if (pipeline.MetadataFilter && route.Prefixes.Count > 0) prefixes = route.Prefixes;
            trace.Add(new StageTrace(Stages.Query, mode == QueryMode.Llm ? "LLM query understanding" : "Rule-based routing", true,
                route.Explanation, ElapsedMs: sw0.ElapsedMilliseconds));
        }

        // ---- 4. Retrieve
        var sw = Stopwatch.StartNew();
        var queryVector = await embeddings.EmbedQueryAsync(question, ct);
        var dense = await DenseAsync(documentId, pipeline, index, queryVector, prefixes, ct);
        if (prefixes is not null && dense.Count < MinRoutedCandidates)
        {
            // The routed sections hold (almost) nothing: never let a routing mistake hide the answer.
            prefixes = null;
            dense = await DenseAsync(documentId, pipeline, index, queryVector, null, ct);
        }
        var filterNote = prefixes is null ? "" : $" Filtered to: {string.Join(" | ", prefixes)}.";
        trace.Add(new StageTrace(Stages.Retrieve, "Dense search (meaning)", true,
            $"Cosine similarity, {denseSearch.Name}, over {index.Chunks.Count} chunks; best {Fmt(dense.FirstOrDefault().Score)}.{filterNote}",
            Hits(index, dense), sw.ElapsedMilliseconds));
        if (pipeline.MetadataFilter)
            trace.Add(new StageTrace(Stages.Retrieve, "Metadata filter", prefixes is not null,
                prefixes is null ? "No filter: searched the whole document." : $"Only chunks under: {string.Join(" | ", prefixes)}."));

        List<(int Index, double Score)> candidates;
        if (pipeline.Hybrid)
        {
            sw.Restart();
            var bm25 = index.Bm25.Search(question, options.CandidateCount, index.SectionFilter(prefixes));
            trace.Add(new StageTrace(Stages.Retrieve, "BM25 (exact words)", true,
                $"Keyword match on: {string.Join(", ", Bm25Index.Tokenize(question).Distinct())}.{filterNote}",
                Hits(index, bm25), sw.ElapsedMilliseconds));

            candidates = ReciprocalRankFusion([dense, bm25], options.RrfK).Take(options.CandidateCount).ToList();
            trace.Add(new StageTrace(Stages.Retrieve, "Reciprocal rank fusion", true,
                $"Merged both lists by rank (k = {options.RrfK}): score = Σ 1/(k + rank).",
                Hits(index, candidates)));
        }
        else
        {
            candidates = dense;
            trace.Add(new StageTrace(Stages.Retrieve, "Hybrid: dense + BM25", false, "Off: dense search only."));
        }

        // ---- 5. Post-process
        List<(int Index, double Score)> ranked;
        bool belowThreshold;
        if (pipeline.Rerank && reranker.IsAvailable)
        {
            sw.Restart();
            // With heading paths on, the reranker also sees where the passage sits ("… > South Asia > Outlook").
            // Only the best fused candidates are reread; the rest keep their fused order behind them.
            var toRerank = candidates.Take(options.RerankCount).ToList();
            var scores = reranker.Score(question, toRerank.Select(c => RerankText(index.Chunks[c.Index], pipeline)).ToList());
            ranked = toRerank.Select((c, i) => (c.Index, (double)scores[i])).OrderByDescending(x => x.Item2).ToList();
            var floor = ranked.Count == 0 ? 0 : ranked[^1].Item2;
            ranked.AddRange(candidates.Skip(toRerank.Count).Select((c, i) => (c.Index, floor - 1 - i)));
            belowThreshold = ranked.Count == 0 || ranked[0].Item2 < options.MinRerankScore;
            trace.Add(new StageTrace(Stages.PostProcess, "Cross-encoder rerank", true,
                $"{reranker.Name} reread the top {Math.Min(options.RerankCount, candidates.Count)} with the question; best score {Fmt(ranked.FirstOrDefault().Item2)}." +
                (belowThreshold ? $" Below threshold {options.MinRerankScore}: refusing." : ""),
                Hits(index, ranked), sw.ElapsedMilliseconds));
        }
        else
        {
            ranked = candidates;
            belowThreshold = dense.Count == 0 || dense[0].Score < options.MinDenseScore;
            trace.Add(new StageTrace(Stages.PostProcess, "Cross-encoder rerank", false,
                pipeline.Rerank ? "Reranker model not installed; skipped." : "Off."));
        }

        var all = ranked.Select(r => new ScoredChunk(index.Chunks[r.Index], r.Item2)).ToList();
        var passages = all.Take(options.ContextCount).ToList();

        if (pipeline.SmallToBig)
        {
            var (expanded, note) = await SmallToBigAsync(documentId, all, ct);
            passages = expanded;
            trace.Add(new StageTrace(Stages.PostProcess, "Small-to-big (parents)", true, note));
        }

        return new RetrievalResult(passages, all, trace, belowThreshold);
    }

    private async Task<List<(int Index, double Score)>> DenseAsync(
        int documentId, PipelineOptions pipeline, ChunkIndex index, float[] queryVector, IReadOnlyList<string>? prefixes, CancellationToken ct) =>
        (await denseSearch.SearchAsync(documentId, pipeline.ChunkSet, queryVector, options.CandidateCount, prefixes, ct))
            .Select(h => (Index: index.IndexOf(h.ChunkId), h.Score)).Where(h => h.Index >= 0).ToList();

    /// <summary>
    /// Search small, answer big: the best children are precise matches, but the LLM gets each child's whole
    /// parent section, so it sees the surrounding context (and the right region's figures) in one piece.
    /// </summary>
    private async Task<(List<ScoredChunk>, string)> SmallToBigAsync(int documentId, List<ScoredChunk> ranked, CancellationToken ct)
    {
        var parents = await indexes.GetAsync(documentId, ChunkSets.Parent, ct);
        var result = new List<ScoredChunk>();
        var seen = new HashSet<Guid>();
        var swapped = 0;
        var budget = options.SmallToBigTokenBudget;

        foreach (var hit in ranked)
        {
            if (result.Count >= options.ContextCount) break;
            var parentIndex = hit.Chunk.ParentId is { } pid ? parents.IndexOf(pid) : -1;
            var chunk = parentIndex >= 0 ? parents.Chunks[parentIndex] : hit.Chunk;
            if (!seen.Add(chunk.Id)) continue;                 // two children of the same section: send it once
            if (budget - chunk.TokenCount < 0 && result.Count > 0) chunk = hit.Chunk;   // keep the prompt bounded
            else if (parentIndex >= 0) swapped++;
            budget -= chunk.TokenCount;
            result.Add(new ScoredChunk(chunk, hit.Score));
        }

        return (result, $"Replaced {swapped} of {result.Count} passages with their parent section " +
                        $"(~{result.Sum(r => r.Chunk.TokenCount)} tokens sent to the LLM); duplicate sections merged.");
    }

    private static string RerankText(Chunk c, PipelineOptions pipeline) =>
        pipeline.HeadingPathEmbedded && c.SectionPath.Length > 0 ? $"{c.SectionPath}: {c.Text}" : c.Text;

    /// <summary>
    /// RRF: each chunk earns 1/(k + rank) from every list it appears in. Only ranks are used,
    /// so a cosine similarity and a BM25 score never need to be on the same scale.
    /// </summary>
    public static List<(int Index, double Score)> ReciprocalRankFusion(IEnumerable<List<(int Index, double Score)>> lists, int k)
    {
        var fused = new Dictionary<int, double>();
        foreach (var list in lists)
            for (var rank = 0; rank < list.Count; rank++)
                fused[list[rank].Index] = fused.GetValueOrDefault(list[rank].Index) + 1.0 / (k + rank + 1);
        return fused.OrderByDescending(x => x.Value).Select(x => (x.Key, x.Value)).ToList();
    }

    private static List<TraceHit> Hits(ChunkIndex index, IEnumerable<(int Index, double Score)> list) =>
        list.Take(10).Select((h, i) =>
        {
            var c = index.Chunks[h.Index];
            return new TraceHit(c.Id, i + 1, Math.Round(h.Score, 4), c.PageNumber, c.SectionPath,
                c.Text.Length > 160 ? c.Text[..160] + "…" : c.Text);
        }).ToList();

    private static string Fmt(double d) => d.ToString("0.###");
}
