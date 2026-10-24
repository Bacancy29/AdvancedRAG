namespace Rag.Retrieval;

public sealed class RagOptions
{
    /// <summary>How many candidates dense search and BM25 each return before fusion.</summary>
    public int CandidateCount { get; set; } = 20;
    /// <summary>How many of the fused candidates the cross-encoder rereads (it runs on the CPU: fewer = faster).</summary>
    public int RerankCount { get; set; } = 12;
    /// <summary>How many passages the LLM sees.</summary>
    public int ContextCount { get; set; } = 5;
    /// <summary>RRF constant. 60 is the value from the original paper.</summary>
    public int RrfK { get; set; } = 60;
    /// <summary>Refuse without calling the LLM when the best reranker logit is below this.</summary>
    public double MinRerankScore { get; set; } = -6;
    /// <summary>Same guard when reranking is off: minimum cosine similarity of the best dense hit.</summary>
    public double MinDenseScore { get; set; } = 0.35;
    /// <summary>Small-to-big: maximum tokens of parent sections sent to the LLM.</summary>
    public int SmallToBigTokenBudget { get; set; } = 2500;
}

public sealed class RerankerOptions
{
    public string ModelPath { get; set; } = "models/ms-marco-MiniLM-L-6-v2/model.onnx";
    public string VocabPath { get; set; } = "models/ms-marco-MiniLM-L-6-v2/vocab.txt";
    /// <summary>Question + passage tokens per pair. Chunks are ~250 tokens, so 320 loses nothing and halves the work.</summary>
    public int MaxTokens { get; set; } = 320;
}
