using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;

namespace Rag.Core;

public sealed class EmbeddingOptions
{
    public string Provider { get; set; } = "Ollama";
    public string Model { get; set; } = "nomic-embed-text";
    public string Endpoint { get; set; } = "http://localhost:11434";
    public string? ApiKey { get; set; }
    /// <summary>nomic-embed-text is trained with task prefixes; leaving them out noticeably hurts retrieval.</summary>
    public string DocumentPrefix { get; set; } = "search_document: ";
    public string QueryPrefix { get; set; } = "search_query: ";
    public int BatchSize { get; set; } = 16;
    /// <summary>Folder for cached document embeddings, so re-ingesting unchanged text costs nothing. Empty = no cache.</summary>
    public string? CacheDirectory { get; set; }
}

/// <summary>Wraps the provider-agnostic embedding generator and applies the model's task prefixes.</summary>
public sealed class EmbeddingService(IEmbeddingGenerator<string, Embedding<float>> generator, EmbeddingOptions options)
{
    private readonly object _cacheLock = new();
    private Dictionary<string, float[]>? _cache;

    public string ModelName => $"{options.Provider}:{options.Model}";

    public async Task<float[]> EmbedQueryAsync(string query, CancellationToken ct = default)
    {
        var result = await generator.GenerateAsync([options.QueryPrefix + query], cancellationToken: ct);
        return Normalize(result[0].Vector.ToArray());
    }

    public async Task<List<float[]>> EmbedDocumentsAsync(
        IReadOnlyList<string> texts, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var inputs = texts.Select(t => options.DocumentPrefix + t).ToList();
        var keys = inputs.Select(Key).ToList();
        var vectors = new float[]?[texts.Count];
        var cache = LoadCache();

        for (var i = 0; i < inputs.Count; i++)
            if (cache.TryGetValue(keys[i], out var hit)) vectors[i] = hit;

        var done = vectors.Count(v => v is not null);
        progress?.Report(done);

        var missing = Enumerable.Range(0, inputs.Count).Where(i => vectors[i] is null).ToList();
        for (var b = 0; b < missing.Count; b += options.BatchSize)
        {
            var batch = missing.Skip(b).Take(options.BatchSize).ToList();
            var result = await generator.GenerateAsync(batch.Select(i => inputs[i]).ToList(), cancellationToken: ct);
            var fresh = new List<(string, float[])>();
            for (var j = 0; j < batch.Count; j++)
            {
                var v = Normalize(result[j].Vector.ToArray());
                vectors[batch[j]] = v;
                fresh.Add((keys[batch[j]], v));
            }
            AppendToCache(fresh);
            done += batch.Count;
            progress?.Report(done);
        }

        return vectors.Select(v => v!).ToList();
    }

    /// <summary>Unit-length vectors make cosine similarity a plain dot product.</summary>
    private static float[] Normalize(float[] v)
    {
        var norm = MathF.Sqrt(v.Sum(x => x * x));
        if (norm == 0) return v;
        for (var i = 0; i < v.Length; i++) v[i] /= norm;
        return v;
    }

    // ---------- cache: one append-only file per model: [32-byte SHA-256][int32 length][float32 × length] ----------

    private string? CacheFile => string.IsNullOrWhiteSpace(options.CacheDirectory)
        ? null
        : Path.Combine(options.CacheDirectory, $"{string.Concat(ModelName.Select(c => char.IsLetterOrDigit(c) ? c : '_'))}.bin");

    private static string Key(string input) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));

    private Dictionary<string, float[]> LoadCache()
    {
        lock (_cacheLock)
        {
            if (_cache is not null) return _cache;
            _cache = [];
            if (CacheFile is not { } file || !File.Exists(file)) return _cache;

            using var reader = new BinaryReader(File.OpenRead(file));
            while (reader.BaseStream.Position < reader.BaseStream.Length)
            {
                var hash = Convert.ToHexString(reader.ReadBytes(32));
                var length = reader.ReadInt32();
                var vector = new float[length];
                for (var i = 0; i < length; i++) vector[i] = reader.ReadSingle();
                _cache[hash] = vector;
            }
            return _cache;
        }
    }

    private void AppendToCache(List<(string Key, float[] Vector)> entries)
    {
        lock (_cacheLock)
        {
            _cache ??= [];
            foreach (var (k, v) in entries) _cache[k] = v;
            if (CacheFile is not { } file) return;

            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using var writer = new BinaryWriter(new FileStream(file, FileMode.Append));
            foreach (var (k, v) in entries)
            {
                writer.Write(Convert.FromHexString(k));
                writer.Write(v.Length);
                foreach (var x in v) writer.Write(x);
            }
        }
    }
}
