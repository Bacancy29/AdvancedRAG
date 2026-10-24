using Rag.Core;
using Rag.Ingestion.Chunking;

namespace Rag.Ingestion;

/// <summary>
/// Offline pipeline: profile strategy → chunks → embeddings → SQL Server.
/// Also builds the naive chunk set so evaluation can compare naive vs advanced on the same document.
/// </summary>
public sealed class IngestionService(
    IEnumerable<IIngestionStrategy> strategies,
    EmbeddingService embeddings,
    IDocumentRepository documents,
    IChunkRepository chunkRepository)
{
    public event Action<int>? DocumentIngested;

    public async Task<IngestionOutput> IngestAsync(int documentId, IProgress<IngestProgress>? progress, CancellationToken ct)
    {
        var doc = await documents.GetAsync(documentId, ct) ?? throw new KeyNotFoundException($"Document {documentId} not found");
        var strategy = strategies.FirstOrDefault(s => s.Profile == doc.Profile)
            ?? throw new NotSupportedException($"No ingestion strategy for profile {doc.Profile} yet");

        doc.Status = DocumentStatus.Ingesting;
        doc.Error = null;
        await documents.UpdateAsync(doc, ct);

        try
        {
            var options = PipelineOptions.ForProfile(doc.Profile);
            var output = await strategy.BuildChunksAsync(doc, options, progress, ct);
            var naive = new NaiveChunker().Chunk(doc.FilePath, doc.Id);

            var all = output.Chunks.Concat(naive).ToList();
            progress?.Report(new IngestProgress("Embed", 40, $"Embedding {all.Count} chunks with {embeddings.ModelName}"));
            var vectors = await embeddings.EmbedDocumentsAsync(
                all.Select(c => EmbeddingText(c, options)).ToList(),
                new Progress<int>(done => progress?.Report(new IngestProgress("Embed", 40 + 50 * done / all.Count, $"Embedded {done}/{all.Count}"))),
                ct);
            for (var i = 0; i < all.Count; i++) all[i].Embedding = vectors[i];

            progress?.Report(new IngestProgress("Store", 95, "Saving chunks and vectors to the database"));
            await chunkRepository.ReplaceAsync(doc.Id, ChunkSets.Advanced, output.Chunks, ct);
            await chunkRepository.ReplaceAsync(doc.Id, ChunkSets.Naive, naive, ct);
            // Parents are looked up by id after search (small-to-big), never searched: no embedding needed.
            await chunkRepository.ReplaceAsync(doc.Id, ChunkSets.Parent, output.Parents ?? [], ct);

            doc.Status = DocumentStatus.Ready;
            doc.PageCount = output.PageCount;
            doc.ChunkCount = output.Chunks.Count;
            doc.EmbeddingModel = embeddings.ModelName;
            doc.IngestedAt = DateTime.UtcNow;
            await documents.UpdateAsync(doc, ct);

            DocumentIngested?.Invoke(doc.Id);
            progress?.Report(new IngestProgress("Done", 100, $"{output.Chunks.Count} chunks ready (+{naive.Count} naive baseline chunks)"));
            return output;
        }
        catch (Exception ex)
        {
            doc.Status = DocumentStatus.Failed;
            doc.Error = ex.Message;
            await documents.UpdateAsync(doc, CancellationToken.None);
            throw;
        }
    }

    /// <summary>Text sent to the embedding model. Long reports also embed the heading path (use case 2).</summary>
    private static string EmbeddingText(Chunk c, PipelineOptions options) =>
        options.HeadingPathEmbedded && c.SectionPath.Length > 0 ? $"{c.SectionPath}: {c.Text}" : c.Text;
}
