using System.Security.Cryptography;
using Microsoft.Extensions.AI;
using PDFtoImage;

namespace Rag.Ingestion.Visuals;

/// <summary>
/// Charts carry their facts in bars, labels and arrows, not in sentences. This renders a PDF page to PNG
/// and asks a vision-capable chat model to write down what the figures show, with every value it can read.
/// Descriptions are cached by image hash: each chart costs one model call, once.
/// </summary>
public sealed class VisionDescriber(IChatClient chat, string pageImageDirectory, string? cacheDirectory)
{
    private const string Prompt = """
        This is one page of a product environmental report. Describe ONLY its charts, diagrams, infographics and
        tables, not the paragraphs of running text. For each figure give its title or topic, then every label and
        value you can read exactly as printed (numbers, units, percentages), and what the figure shows (totals,
        breakdowns, comparisons). Do not guess values you cannot read. Plain prose or short lists, under 250 words.
        If the page has no figures, reply exactly: NO FIGURES
        """;

    /// <summary>Renders the page (kept for the UI's chart thumbnails) and returns its image path.</summary>
    public string RenderPage(string pdfPath, int documentId, int pageNumber)
    {
        var folder = Path.Combine(pageImageDirectory, documentId.ToString());
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"p{pageNumber}.png");
        if (!File.Exists(file))
        {
            using var pdf = File.OpenRead(pdfPath);
            Conversion.SavePng(file, pdf, page: pageNumber - 1, leaveOpen: false, options: new RenderOptions(Dpi: 110));
        }
        return file;
    }

    /// <summary>Returns the description, or null when the page has no figures.</summary>
    public async Task<string?> DescribeAsync(string imagePath, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(imagePath, ct);
        var cacheFile = cacheDirectory is null ? null : Path.Combine(cacheDirectory, Convert.ToHexString(SHA256.HashData(bytes))[..32] + ".txt");
        string text;
        if (cacheFile is not null && File.Exists(cacheFile))
        {
            text = await File.ReadAllTextAsync(cacheFile, ct);
        }
        else
        {
            var response = await chat.GetResponseAsync(
                [new ChatMessage(ChatRole.User, [new TextContent(Prompt), new DataContent(bytes, "image/png")])],
                new ChatOptions { Temperature = 0 }, ct);
            text = response.Text.Trim();
            if (cacheFile is not null && text.Length > 0)
            {
                Directory.CreateDirectory(cacheDirectory!);
                await File.WriteAllTextAsync(cacheFile, text, ct);
            }
        }
        return text.Length == 0 || text.StartsWith("NO FIGURES", StringComparison.OrdinalIgnoreCase) ? null : text;
    }
}
