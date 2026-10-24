using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Rag.Core;
using Rag.Retrieval;

namespace Rag.Hosting;

/// <summary>
/// Pays the one-time costs at startup instead of on the first question: loading each document's chunk index,
/// starting the ONNX reranker, and loading the embedding model into Ollama.
/// </summary>
public sealed class WarmUp(
    IDocumentRepository documents,
    ChunkIndexCache indexes,
    EmbeddingService embeddings,
    IReranker reranker,
    ILogger<WarmUp> logger)
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await embeddings.EmbedQueryAsync("warm up", ct);
            if (reranker.IsAvailable) reranker.Score("warm up", ["warm up"]);

            foreach (var doc in (await documents.ListAsync(ct)).Where(d => d.Status == DocumentStatus.Ready))
            {
                await indexes.GetAsync(doc.Id, ChunkSets.Advanced, ct);
                if (PipelineOptions.ForProfile(doc.Profile).SmallToBig) await indexes.GetAsync(doc.Id, ChunkSets.Parent, ct);
            }
            logger.LogInformation("Warm-up finished in {Ms} ms", sw.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Warm-up is an optimisation only; the first question simply pays the cost instead.
            logger.LogWarning("Warm-up skipped: {Error}", ex.Message.Split('\n')[0]);
        }
    }
}
