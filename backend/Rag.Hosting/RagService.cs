using System.Runtime.CompilerServices;
using System.Text.Json;
using Rag.Core;
using Rag.Generation;
using Rag.Retrieval;

namespace Rag.Hosting;

/// <summary>Answers a question about one document: retrieval stages 3–5, then generation (stage 6).</summary>
public sealed class RagService(
    IDocumentRepository documents,
    IMessageRepository messages,
    RetrievalPipeline retrieval,
    AnswerGenerator generator)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async IAsyncEnumerable<GenerationEvent> AskStreamAsync(
        int documentId, string question, bool naive = false, bool saveHistory = true,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var doc = await documents.GetAsync(documentId, ct) ?? throw new KeyNotFoundException($"Document {documentId} not found");
        if (doc.Status != DocumentStatus.Ready) throw new InvalidOperationException($"'{doc.Name}' is not ingested yet.");

        var options = naive ? PipelineOptions.Naive : PipelineOptions.ForProfile(doc.Profile);
        var result = await retrieval.RetrieveAsync(documentId, question, options, ct);

        if (saveHistory) await messages.AddAsync(documentId, "user", question, null, ct);

        await foreach (var e in generator.StreamAsync(doc.Name, question, result, ct))
        {
            if (e is CompletedEvent done && saveHistory)
            {
                var meta = JsonSerializer.Serialize(new { done.Answer.Refused, done.Answer.Citations, done.Answer.Trace }, Json);
                await messages.AddAsync(documentId, "assistant", done.Answer.Answer, meta, CancellationToken.None);
            }
            yield return e;
        }
    }

    public async Task<RagAnswer> AskAsync(int documentId, string question, bool naive = false, bool saveHistory = false, CancellationToken ct = default)
    {
        RagAnswer? answer = null;
        await foreach (var e in AskStreamAsync(documentId, question, naive, saveHistory, ct))
            if (e is CompletedEvent done) answer = done.Answer;
        return answer!;
    }
}
