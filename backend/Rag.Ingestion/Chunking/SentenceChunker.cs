using System.Text;
using Rag.Core;
using Rag.Ingestion.Layout;

namespace Rag.Ingestion.Chunking;

public sealed record SentenceChunkerOptions(int TargetTokens = 250, int MaxTokens = 380, int OverlapSentences = 2, int MaxOverlapTokens = 80, int MinTokens = 60);

/// <summary>
/// Packs whole sentences into chunks of about <see cref="SentenceChunkerOptions.TargetTokens"/>,
/// repeating the last sentences of each chunk at the start of the next one (overlap) so a fact
/// that sits on a boundary keeps its context. A new section always starts a new chunk.
/// </summary>
public sealed class SentenceChunker(SentenceChunkerOptions options)
{
    public List<Chunk> Chunk(IReadOnlyList<LayoutSegment> segments, int documentId)
    {
        var chunks = new List<Chunk>();
        var flow = new FlowBuffer();
        string? h1 = null, h2 = null;
        string Section() => h2 is null ? h1 ?? "" : h1 is null ? h2 : $"{h1} > {h2}";
        var asides = new List<LayoutSegment>();
        var asideTokens = 0;
        var asideSection = "";

        foreach (var seg in segments)
        {
            switch (seg.Kind)
            {
                case SegmentKind.Heading:
                    var newH1 = seg.HeadingLevel == 1 ? seg.Text : h1;
                    var newH2 = seg.HeadingLevel == 1 ? null : seg.Text;
                    if (newH1 == h1 && newH2 == h2) break;   // repeated page title
                    Flush(flow, Section(), chunks, documentId);
                    (h1, h2) = (newH1, newH2);
                    break;

                case SegmentKind.Body:
                    flow.Append(seg.Text, seg.Page);
                    break;

                case SegmentKind.Table:
                    // A table is one chunk: splitting it would separate labels from their values.
                    chunks.Add(Make(documentId, seg.Text, seg.Page, seg.Page, Section(), ContentTypes.Table));
                    break;

                case SegmentKind.Aside:
                    // Captions and panels stay out of the main flow so they never split one of its sentences.
                    // Asides on the same page are grouped, so a panel title ("GPO By the Numbers FY 2018")
                    // travels with its values instead of becoming a fragment nobody retrieves.
                    if (asides.Count > 0 && (asides[^1].Page != seg.Page || asideTokens + TokenEstimator.Estimate(seg.Text) > options.TargetTokens))
                        FlushAsides();
                    if (asides.Count == 0) asideSection = Section();
                    asides.Add(seg);
                    asideTokens += TokenEstimator.Estimate(seg.Text);
                    break;
            }
        }

        Flush(flow, Section(), chunks, documentId);
        FlushAsides();

        void FlushAsides()
        {
            var text = string.Join(" ", asides.Select(a => a.Text.TrimEnd('.') + "."));
            if (asides.Count > 0 && text.Split(' ').Length >= 5)
                chunks.Add(Make(documentId, text, asides[0].Page, asides[^1].Page, asideSection, ContentTypes.Aside));
            asides.Clear();
            asideTokens = 0;
        }

        for (var i = 0; i < chunks.Count; i++) chunks[i].ChunkIndex = i;
        return chunks;
    }

    private void Flush(FlowBuffer flow, string section, List<Chunk> chunks, int documentId)
    {
        var text = flow.ToString();
        if (text.Trim().Length == 0)
        {
            flow.Clear();
            return;
        }

        var sentences = SentenceSplitter.Split(text)
            .SelectMany(r => SplitLong(text, r))
            .Select(r => new Sentence(text[r.Start..r.End].Trim(), flow.PageAt(r.Start), flow.PageAt(Math.Max(r.Start, r.End - 1))))
            .Where(s => s.Text.Length > 0)
            .ToList();

        var produced = new List<Chunk>();
        var current = new List<Sentence>();
        var currentTokens = 0;
        var newSentences = 0;   // sentences not carried over as overlap

        foreach (var s in sentences)
        {
            var t = TokenEstimator.Estimate(s.Text);
            if (current.Count > 0 && newSentences > 0 && currentTokens + t > options.TargetTokens)
            {
                produced.Add(Make(documentId, current, section));
                current = Overlap(current);
                currentTokens = current.Sum(x => TokenEstimator.Estimate(x.Text));
                newSentences = 0;
            }
            current.Add(s);
            currentTokens += t;
            newSentences++;
        }

        if (newSentences > 0)
        {
            var last = Make(documentId, current, section);
            // Merge a tiny tail into the previous chunk instead of leaving a fragment.
            if (produced.Count > 0 && last.TokenCount < options.MinTokens)
            {
                var prev = produced[^1];
                var tail = current.Skip(current.Count - newSentences).ToList();
                var merged = prev.Text + " " + string.Join(" ", tail.Select(x => x.Text));
                if (TokenEstimator.Estimate(merged) <= options.MaxTokens + 100)
                {
                    prev.Text = merged;
                    prev.TokenCount = TokenEstimator.Estimate(merged);
                    prev.PageEnd = Math.Max(prev.PageEnd, tail[^1].PageEnd);
                }
                else produced.Add(last);
            }
            else produced.Add(last);
        }

        chunks.AddRange(produced);
        flow.Clear();
    }

    private List<Sentence> Overlap(List<Sentence> sentences)
    {
        var carry = new List<Sentence>();
        var tokens = 0;
        for (var i = sentences.Count - 1; i >= 0 && carry.Count < options.OverlapSentences; i--)
        {
            var t = TokenEstimator.Estimate(sentences[i].Text);
            if (tokens + t > options.MaxOverlapTokens) break;
            carry.Insert(0, sentences[i]);
            tokens += t;
        }
        return carry;
    }

    /// <summary>Very long "sentences" (lists, tables flattened to text) are cut at word boundaries.</summary>
    private IEnumerable<(int Start, int End)> SplitLong(string text, (int Start, int End) range)
    {
        var maxChars = options.MaxTokens * 4;
        var (start, end) = range;
        while (end - start > maxChars)
        {
            var cut = text.LastIndexOf(' ', start + maxChars, maxChars / 2);
            if (cut <= start) cut = start + maxChars;
            yield return (start, cut);
            start = cut + 1;
        }
        yield return (start, end);
    }

    private static Chunk Make(int documentId, List<Sentence> sentences, string section) =>
        Make(documentId, string.Join(" ", sentences.Select(s => s.Text)), sentences[0].Page, sentences[^1].PageEnd, section, ContentTypes.Text);

    private static Chunk Make(int documentId, string text, int page, int pageEnd, string section, string contentType) => new()
    {
        DocumentId = documentId,
        ChunkSet = ChunkSets.Advanced,
        ContentType = contentType,
        PageNumber = page,
        PageEnd = pageEnd,
        SectionPath = section,
        Text = text,
        TokenCount = TokenEstimator.Estimate(text),
    };

    private sealed record Sentence(string Text, int Page, int PageEnd);
}
