using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Rag.Retrieval;

public interface IReranker
{
    bool IsAvailable { get; }
    string Name { get; }
    /// <summary>Returns one relevance score (logit) per passage, in input order.</summary>
    float[] Score(string query, IReadOnlyList<string> passages);
}

/// <summary>
/// Cross-encoder (ms-marco-MiniLM-L-6-v2) run locally with ONNX Runtime. Unlike embeddings, which
/// compare two separately computed vectors, it reads the question and the passage together,
/// so it judges relevance much more precisely. Too slow for a whole document, ideal for the top 20.
/// </summary>
public sealed class CrossEncoderReranker : IReranker, IDisposable
{
    private readonly InferenceSession? _session;
    private readonly BertTokenizer? _tokenizer;
    private readonly int _maxTokens;

    public CrossEncoderReranker(RerankerOptions options, ILogger<CrossEncoderReranker> logger)
    {
        _maxTokens = options.MaxTokens;
        if (!File.Exists(options.ModelPath) || !File.Exists(options.VocabPath))
        {
            logger.LogWarning("Reranker model not found at {Path}; reranking disabled", options.ModelPath);
            return;
        }
        _session = new InferenceSession(options.ModelPath);
        _tokenizer = BertTokenizer.Create(options.VocabPath, new BertOptions { LowerCaseBeforeTokenization = true });
    }

    public bool IsAvailable => _session is not null;
    public string Name => "ms-marco-MiniLM-L-6-v2 (ONNX)";

    public float[] Score(string query, IReadOnlyList<string> passages)
    {
        if (_session is null || _tokenizer is null) throw new InvalidOperationException("Reranker model not loaded");
        if (passages.Count == 0) return [];

        var q = _tokenizer.EncodeToIds(query, addSpecialTokens: false).ToList();
        var pairs = passages.Select(p =>
        {
            var ids = _tokenizer.EncodeToIds(p, addSpecialTokens: false).ToList();
            var room = Math.Max(16, _maxTokens - q.Count - 3);   // [CLS] q [SEP] p [SEP]
            if (ids.Count > room) ids = ids.Take(room).ToList();
            return (Ids: _tokenizer.BuildInputsWithSpecialTokens(q, ids).ToArray(),
                    Types: _tokenizer.CreateTokenTypeIdsFromSequences(q, ids).ToArray());
        }).ToList();

        var len = pairs.Max(p => p.Ids.Length);
        var shape = new[] { pairs.Count, len };
        var inputIds = new DenseTensor<long>(shape);
        var mask = new DenseTensor<long>(shape);
        var types = new DenseTensor<long>(shape);
        for (var i = 0; i < pairs.Count; i++)
        {
            for (var j = 0; j < pairs[i].Ids.Length; j++)
            {
                inputIds[i, j] = pairs[i].Ids[j];
                mask[i, j] = 1;
                types[i, j] = pairs[i].Types[j];
            }
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", mask),
        };
        if (_session.InputMetadata.ContainsKey("token_type_ids"))
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", types));

        using var results = _session.Run(inputs);
        var logits = results.First().AsTensor<float>();
        return Enumerable.Range(0, pairs.Count).Select(i => logits[i, 0]).ToArray();
    }

    public void Dispose() => _session?.Dispose();
}
