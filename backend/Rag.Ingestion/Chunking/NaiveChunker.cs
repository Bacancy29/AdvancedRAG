using System.Text;
using Rag.Core;
using UglyToad.PdfPig;

namespace Rag.Ingestion.Chunking;

/// <summary>
/// The baseline most tutorials use: raw text in PDF content order, cut every N characters.
/// Kept only so the evaluation can show what the advanced pipeline improves.
/// </summary>
public sealed class NaiveChunker(int chunkChars = 1000)
{
    public List<Chunk> Chunk(string pdfPath, int documentId)
    {
        using var pdf = PdfDocument.Open(pdfPath);
        var text = new StringBuilder();
        var pageStarts = new List<(int Offset, int Page)>();

        foreach (var page in pdf.GetPages())
        {
            pageStarts.Add((text.Length, page.Number));
            text.Append(string.Join(" ", page.GetWords().Select(w => w.Text))).Append(' ');
        }

        var all = text.ToString();
        var chunks = new List<Chunk>();
        for (var start = 0; start < all.Length; start += chunkChars)
        {
            var piece = all.Substring(start, Math.Min(chunkChars, all.Length - start)).Trim();
            if (piece.Length < 20) continue;
            chunks.Add(new Chunk
            {
                DocumentId = documentId,
                ChunkSet = ChunkSets.Naive,
                ChunkIndex = chunks.Count,
                PageNumber = PageAt(pageStarts, start),
                PageEnd = PageAt(pageStarts, Math.Min(all.Length - 1, start + chunkChars - 1)),
                Text = piece,
                TokenCount = TokenEstimator.Estimate(piece),
            });
        }
        return chunks;
    }

    private static int PageAt(List<(int Offset, int Page)> starts, int offset) =>
        starts.LastOrDefault(s => s.Offset <= offset).Page;
}
