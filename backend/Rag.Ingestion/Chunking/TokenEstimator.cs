namespace Rag.Ingestion.Chunking;

/// <summary>About 4 characters per token for English text; close enough for sizing chunks.</summary>
public static class TokenEstimator
{
    public static int Estimate(string text) => (text.Length + 3) / 4;
}
