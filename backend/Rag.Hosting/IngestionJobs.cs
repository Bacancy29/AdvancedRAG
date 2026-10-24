using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rag.Core;
using Rag.Ingestion;

namespace Rag.Hosting;

/// <summary>Runs ingestion in the background and keeps the latest progress for the UI to poll.</summary>
public sealed class IngestionJobs(IServiceScopeFactory scopes, ILogger<IngestionJobs> logger)
{
    private readonly ConcurrentDictionary<int, IngestProgress> _progress = new();

    public IngestProgress? Get(int documentId) => _progress.GetValueOrDefault(documentId);

    public bool Start(int documentId)
    {
        if (_progress.TryGetValue(documentId, out var p) && p.Percent is > 0 and < 100 && p.Stage != "Failed") return false;
        _progress[documentId] = new IngestProgress("Queued", 1, "Starting");

        _ = Task.Run(async () =>
        {
            using var scope = scopes.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IngestionService>();
            try
            {
                await service.IngestAsync(documentId, new Progress<IngestProgress>(x => _progress[documentId] = x), CancellationToken.None);
                _progress[documentId] = new IngestProgress("Done", 100, _progress[documentId].Message);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ingestion failed for document {Id}", documentId);
                _progress[documentId] = new IngestProgress("Failed", 100, ex.Message);
            }
        });
        return true;
    }
}
