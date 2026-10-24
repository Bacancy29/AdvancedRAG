using Rag.Core;
using Rag.Ingestion.Layout;

namespace Rag.Ingestion.Chunking;

public sealed record ParentChildOptions(
    int ParentTokens = 900,
    int ChildTokens = 250,
    int OverlapSentences = 1,
    int MaxOverlapTokens = 60);

public sealed record HierarchicalChunks(List<Chunk> Children, List<Chunk> Parents);

/// <summary>
/// Long, structured documents. Every chunk carries its full location ("Chapter 2: Regional Outlooks >
/// South Asia > Outlook"), so "growth in South Asia" and "growth in Sub-Saharan Africa" can be told apart.
/// <list type="bullet">
/// <item>Parents: whole sections (up to ~900 tokens). Stored, not searched; sent to the LLM (small-to-big).</item>
/// <item>Children: ~250-token pieces of a parent, whole sentences with overlap. These are searched.</item>
/// <item>Boxed articles are a separate stream: a chapter sentence that continues after a box page stays intact.</item>
/// </list>
/// </summary>
public sealed class ParentChildChunker(ParentChildOptions options)
{
    private const string MainStream = "";

    public HierarchicalChunks Chunk(IReadOnlyList<LayoutSegment> segments, DocumentOutline outline, int documentId)
    {
        var children = new List<Chunk>();
        var parents = new List<Chunk>();
        var streams = new Dictionary<string, StreamState>();
        StreamState Stream(string key) => streams.TryGetValue(key, out var s) ? s : streams[key] = new StreamState();

        var chapter = "";
        var page = -1;
        var active = MainStream;
        var asides = new List<LayoutSegment>();

        string PathOf(string key, StreamState s) =>
            string.Join(" > ", new[] { chapter, key, s.H2, s.H3 }.Where(x => !string.IsNullOrEmpty(x)));

        void FlushAsides()
        {
            if (asides.Count == 0) return;
            var text = string.Join(" ", asides.Select(a => a.Text.TrimEnd('.') + "."));
            if (text.Split(' ').Length >= 8)
                children.Add(Make(documentId, text, asides[0].Page, asides[^1].Page, PathOf(active, Stream(active)), ContentTypes.Aside, null));
            asides.Clear();
        }

        foreach (var seg in segments)
        {
            if (seg.Page != page)
            {
                FlushAsides();
                page = seg.Page;
                active = MainStream;   // a page is chapter text unless it carries a box marker
                var newChapter = outline.ChapterOf(page);
                if (newChapter != chapter)
                {
                    foreach (var (key, s) in streams) Flush(s, documentId, children, parents);
                    streams.Clear();
                    chapter = newChapter;
                }
            }

            var stream = Stream(active);
            switch (seg.Kind)
            {
                case SegmentKind.BoxMarker:
                    active = seg.Text;
                    break;

                case SegmentKind.Heading when seg.HeadingLevel is 2 or 3:
                    var (h2, h3) = seg.HeadingLevel == 2 ? (seg.Text, (string?)null) : (stream.H2, seg.Text);
                    if (h2 == stream.H2 && h3 == stream.H3) break;
                    Flush(stream, documentId, children, parents);
                    (stream.H2, stream.H3) = (h2, h3);
                    break;

                case SegmentKind.Heading when seg.HeadingLevel >= 4:
                    // Bold lead-in ("A Rising Challenge: … in EMDEs."): part of the sentence flow, not a section.
                    Append(stream, seg.Text, seg.Page, PathOf(active, stream));
                    break;

                case SegmentKind.Heading:
                    break;   // title pages: chapter names come from the bookmarks

                case SegmentKind.Body:
                    Append(stream, seg.Text, seg.Page, PathOf(active, stream));
                    break;

                case SegmentKind.Aside:
                    asides.Add(seg);
                    break;
            }
        }

        FlushAsides();
        foreach (var s in streams.Values) Flush(s, documentId, children, parents);

        for (var i = 0; i < children.Count; i++) children[i].ChunkIndex = i;
        for (var i = 0; i < parents.Count; i++) parents[i].ChunkIndex = i;
        return new HierarchicalChunks(children, parents);
    }

    private static void Append(StreamState s, string text, int page, string path)
    {
        if (s.Buffer.IsEmpty) s.Section = path;
        s.Buffer.Append(text, page);
    }

    private void Flush(StreamState s, int documentId, List<Chunk> children, List<Chunk> parents)
    {
        if (s.Buffer.IsEmpty)
        {
            s.Buffer.Clear();
            return;
        }

        var text = s.Buffer.ToString();
        var sentences = SentenceSplitter.Split(text)
            .Select(r => new Sentence(text[r.Start..r.End].Trim(), s.Buffer.PageAt(r.Start), s.Buffer.PageAt(Math.Max(r.Start, r.End - 1))))
            .Where(x => x.Text.Length > 0)
            .ToList();
        s.Buffer.Clear();

        foreach (var group in Pack(sentences, options.ParentTokens, overlap: false))
        {
            var parent = Make(documentId, Join(group), group[0].Page, group[^1].PageEnd, s.Section, ContentTypes.Text, null);
            parent.ChunkSet = ChunkSets.Parent;
            parents.Add(parent);

            foreach (var piece in Pack(group, options.ChildTokens, overlap: true))
                children.Add(Make(documentId, Join(piece), piece[0].Page, piece[^1].PageEnd, s.Section, ContentTypes.Text, parent.Id));
        }
    }

    /// <summary>Greedy packing of whole sentences up to a token budget, optionally repeating the last sentence(s).</summary>
    private IEnumerable<List<Sentence>> Pack(List<Sentence> sentences, int budget, bool overlap)
    {
        var current = new List<Sentence>();
        var tokens = 0;
        var fresh = 0;
        foreach (var x in sentences)
        {
            var t = TokenEstimator.Estimate(x.Text);
            if (fresh > 0 && tokens + t > budget)
            {
                yield return current;
                current = overlap ? Tail(current) : [];
                tokens = current.Sum(c => TokenEstimator.Estimate(c.Text));
                fresh = 0;
            }
            current.Add(x);
            tokens += t;
            fresh++;
        }
        if (fresh > 0) yield return current;
    }

    private List<Sentence> Tail(List<Sentence> sentences)
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

    private static string Join(List<Sentence> sentences) => string.Join(" ", sentences.Select(x => x.Text));

    private static Chunk Make(int documentId, string text, int page, int pageEnd, string section, string contentType, Guid? parentId) => new()
    {
        DocumentId = documentId,
        ChunkSet = ChunkSets.Advanced,
        ParentId = parentId,
        ContentType = contentType,
        PageNumber = page,
        PageEnd = pageEnd,
        SectionPath = section,
        Text = text,
        TokenCount = TokenEstimator.Estimate(text),
    };

    private sealed record Sentence(string Text, int Page, int PageEnd);

    private sealed class StreamState
    {
        public string? H2 { get; set; }
        public string? H3 { get; set; }
        public string Section { get; set; } = "";
        public FlowBuffer Buffer { get; } = new();
    }
}
