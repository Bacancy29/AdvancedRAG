using System.Collections.Concurrent;
using Rag.Core;

namespace Rag.Retrieval;

/// <summary>
/// In-memory index for one document's chunk set: the chunks, a BM25 keyword index, and (for the
/// in-memory vector store) their normalized vectors.
/// </summary>
public sealed class ChunkIndex
{
    private readonly Dictionary<Guid, int> _positions;

    public ChunkIndex(IReadOnlyList<Chunk> chunks)
    {
        Chunks = chunks;
        Bm25 = new Bm25Index(chunks.Select(c => $"{c.SectionPath} {c.Text}"));
        _positions = chunks.Select((c, i) => (c.Id, i)).ToDictionary(x => x.Id, x => x.i);
    }

    public IReadOnlyList<Chunk> Chunks { get; }
    public Bm25Index Bm25 { get; }

    public int IndexOf(Guid chunkId) => _positions.TryGetValue(chunkId, out var i) ? i : -1;

    /// <summary>Exact search: compare the query with every vector. Vectors are unit length, so dot product = cosine.</summary>
    public List<(int Index, double Score)> DenseSearch(float[] query, int top, Func<int, bool>? allow = null)
    {
        var scores = new List<(int, double)>(Chunks.Count);
        for (var i = 0; i < Chunks.Count; i++)
        {
            if (allow is not null && !allow(i)) continue;
            var v = Chunks[i].Embedding;
            double dot = 0;
            for (var d = 0; d < v.Length && d < query.Length; d++) dot += v[d] * query[d];
            scores.Add((i, dot));
        }
        return scores.OrderByDescending(s => s.Item2).Take(top).ToList();
    }

    /// <summary>Chunk positions allowed by a section filter, or null when there is no filter.</summary>
    public Func<int, bool>? SectionFilter(IReadOnlyList<string>? prefixes) =>
        prefixes is not { Count: > 0 } ? null
        : i => prefixes.Any(p => Chunks[i].SectionPath.StartsWith(p, StringComparison.Ordinal));
}

public sealed class ChunkIndexCache(IChunkRepository repository, bool loadVectors)
{
    private readonly ConcurrentDictionary<(int, string), Lazy<Task<ChunkIndex>>> _cache = new();

    public Task<ChunkIndex> GetAsync(int documentId, string chunkSet, CancellationToken ct = default) =>
        _cache.GetOrAdd((documentId, chunkSet), key => new Lazy<Task<ChunkIndex>>(async () =>
            new ChunkIndex(await repository.GetAsync(key.Item1, key.Item2, includeEmbeddings: loadVectors, ct)))).Value;

    public void Invalidate(int documentId)
    {
        foreach (var key in _cache.Keys.Where(k => k.Item1 == documentId).ToList()) _cache.TryRemove(key, out _);
    }
}

/// <summary>Vectors stored as bytes in SQL Server 2022 (no vector type), searched exactly in C#.</summary>
public sealed class InMemoryDenseSearch(ChunkIndexCache cache) : IDenseSearch
{
    public string Name => "exact cosine in C# (vectors from SQL Server)";
    public bool NeedsVectorsInMemory => true;

    public async Task<IReadOnlyList<(Guid ChunkId, double Score)>> SearchAsync(
        int documentId, string chunkSet, float[] query, int top, IReadOnlyList<string>? sectionPrefixes = null, CancellationToken ct = default)
    {
        var index = await cache.GetAsync(documentId, chunkSet, ct);
        return index.DenseSearch(query, top, index.SectionFilter(sectionPrefixes)).Select(h => (index.Chunks[h.Index].Id, h.Score)).ToList();
    }
}
