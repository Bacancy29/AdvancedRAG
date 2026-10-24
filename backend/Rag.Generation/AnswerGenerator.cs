using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Rag.Core;

namespace Rag.Generation;

public sealed class ChatModelOptions
{
    public string Provider { get; set; } = "Gemini";
    public string Model { get; set; } = "gemini-3.5-flash";
    public string? Endpoint { get; set; }
    public string? ApiKey { get; set; }
    public float Temperature { get; set; } = 0;
    /// <summary>"Thinking" budget for models that reason before answering (low = faster for short factual answers). Empty = model default.</summary>
    public string? ReasoningEffort { get; set; } = "low";
    /// <summary>SDK retries on 429/5xx. Kept low so a used-up quota fails fast with a clear message.</summary>
    public int MaxRetries { get; set; } = 1;
    public int TimeoutSeconds { get; set; } = 60;
}

/// <summary>Streaming output: text tokens first, then the final answer with citations.</summary>
public abstract record GenerationEvent;
public sealed record TokenEvent(string Text) : GenerationEvent;
public sealed record CompletedEvent(RagAnswer Answer) : GenerationEvent;
/// <summary>Sent as soon as retrieval is done, before the LLM starts: the UI can show the sources while the answer is written.</summary>
public sealed record RetrievedEvent(IReadOnlyList<Citation> Citations, IReadOnlyList<StageTrace> Trace) : GenerationEvent;

/// <summary>
/// Stage 6: grounded answer. The model sees numbered passages and must cite them,
/// copy facts exactly, or refuse with a fixed sentence.
/// </summary>
public sealed partial class AnswerGenerator(IChatClient chat, ChatModelOptions options)
{
    public const string RefusalText = "I could not find this in the document.";

    public string ModelName => $"{options.Provider}:{options.Model}";

    public async IAsyncEnumerable<GenerationEvent> StreamAsync(
        string documentName,
        string question,
        RetrievalResult retrieval,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var trace = retrieval.Trace.ToList();
        var sw = Stopwatch.StartNew();

        // Guardrail: nothing relevant retrieved → refuse without asking the LLM (no chance to hallucinate).
        if (retrieval.BelowThreshold || retrieval.Passages.Count == 0)
        {
            yield return new TokenEvent(RefusalText);
            trace.Add(new StageTrace(Stages.Generate, "Grounded prompt, citations, refusal", true,
                "Refused before calling the LLM: no passage was relevant enough."));
            yield return new CompletedEvent(new RagAnswer(RefusalText, true, Citations(retrieval, ""), trace));
            yield break;
        }

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt(documentName)),
            new(ChatRole.User, UserPrompt(question, retrieval.Passages)),
        };
        var chatOptions = new ChatOptions { Temperature = options.Temperature, ModelId = options.Model };
        yield return new RetrievedEvent(Citations(retrieval, ""), trace);

        var answer = new StringBuilder();
        await foreach (var update in chat.GetStreamingResponseAsync(messages, chatOptions, ct))
        {
            if (string.IsNullOrEmpty(update.Text)) continue;
            answer.Append(update.Text);
            yield return new TokenEvent(update.Text);
        }

        var text = answer.ToString().Trim();
        var refused = text.Contains("could not find this in the document", StringComparison.OrdinalIgnoreCase);
        trace.Add(new StageTrace(Stages.Generate, "Grounded prompt, citations, refusal", true,
            $"{ModelName} answered from {retrieval.Passages.Count} numbered passages" +
            (refused ? " and refused: the passages do not contain the answer." : $"; cited [{string.Join("], [", CitedNumbers(text))}]."),
            ElapsedMs: sw.ElapsedMilliseconds));

        yield return new CompletedEvent(new RagAnswer(text, refused, Citations(retrieval, text), trace));
    }

    public async Task<RagAnswer> AnswerAsync(string documentName, string question, RetrievalResult retrieval, CancellationToken ct = default)
    {
        RagAnswer? result = null;
        await foreach (var e in StreamAsync(documentName, question, retrieval, ct))
            if (e is CompletedEvent done) result = done.Answer;
        return result!;
    }

    private static string SystemPrompt(string documentName) => $"""
        You answer questions about one document: "{documentName}".
        You are given numbered passages retrieved from it. Follow these rules strictly:
        1. Use ONLY facts stated in the passages. Never use outside knowledge, even if you know the answer.
        2. Copy numbers, amounts, dates and names exactly as written in the passages. When asked "when", give the
           specific date or month the passage states (e.g. "March 2017"), not a vaguer period.
        3. Cite the passage number after each fact, like [1] or [2][3].
        4. If the passages do not contain the answer, reply exactly: "{RefusalText}"
        5. If they answer only part of the question, answer that part and say which part is not in the document.
        6. Be concise: one to three sentences. Do not add commentary or background.
        """;

    private static string UserPrompt(string question, IReadOnlyList<ScoredChunk> passages)
    {
        var sb = new StringBuilder("Passages:\n\n");
        for (var i = 0; i < passages.Count; i++)
        {
            var c = passages[i].Chunk;
            var section = c.SectionPath.Length > 0 ? $", section: {c.SectionPath}" : "";
            sb.Append($"[{i + 1}] (page {c.PageNumber}{section})\n{c.Text}\n\n");
        }
        sb.Append($"Question: {question}");
        return sb.ToString();
    }

    private static List<Citation> Citations(RetrievalResult retrieval, string answer)
    {
        var cited = CitedNumbers(answer).ToHashSet();
        return retrieval.Passages.Select((p, i) => new Citation(i + 1, p.Chunk.Id, p.Chunk.PageNumber, p.Chunk.SectionPath,
            p.Chunk.ContentType, p.Chunk.Text, Math.Round(p.Score, 4), cited.Contains(i + 1))).ToList();
    }

    private static IEnumerable<int> CitedNumbers(string text) =>
        CitationRegex().Matches(text).Select(m => int.Parse(m.Groups[1].Value)).Distinct().Order();

    [GeneratedRegex(@"\[(\d+)\]")]
    private static partial Regex CitationRegex();
}
