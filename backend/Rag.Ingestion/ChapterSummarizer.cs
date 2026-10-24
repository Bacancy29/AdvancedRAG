using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;

namespace Rag.Ingestion;

/// <summary>
/// One short summary per chapter, written by the chat model from the chapter's own text. Summaries answer
/// broad questions ("What does Chapter 3 say about…?") that no single 250-token chunk can. Results are
/// cached on disk by content hash, so re-ingesting costs no extra LLM calls.
/// </summary>
public sealed class ChapterSummarizer(IChatClient chat, string? cacheDirectory)
{
    private const int MaxInputChars = 60_000;   // ~15k tokens: the chapter's opening summary and first sections

    public async Task<string> SummarizeAsync(string documentName, string chapter, string chapterText, CancellationToken ct)
    {
        var input = chapterText.Length > MaxInputChars ? chapterText[..MaxInputChars] : chapterText;
        var cacheFile = CacheFile(chapter, input);
        if (cacheFile is not null && File.Exists(cacheFile)) return await File.ReadAllTextAsync(cacheFile, ct);

        var response = await chat.GetResponseAsync(
        [
            new ChatMessage(ChatRole.System,
                "You summarize one chapter of a report. Use ONLY the text provided. Keep numbers exactly as written. " +
                "Write 150-200 words of plain prose: the chapter's main topic, key findings with their figures, and policy conclusions. " +
                "Do not add facts that are not in the text."),
            new ChatMessage(ChatRole.User, $"Report: {documentName}\nChapter: {chapter}\n\nChapter text:\n{input}"),
        ], new ChatOptions { Temperature = 0 }, ct);

        var summary = response.Text.Trim();
        if (cacheFile is not null && summary.Length > 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
            await File.WriteAllTextAsync(cacheFile, summary, ct);
        }
        return summary;
    }

    private string? CacheFile(string chapter, string input) => string.IsNullOrWhiteSpace(cacheDirectory)
        ? null
        : Path.Combine(cacheDirectory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(chapter + "\n" + input)))[..32] + ".txt");
}
