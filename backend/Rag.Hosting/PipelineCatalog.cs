using Rag.Core;

namespace Rag.Hosting;

public sealed record TechniqueInfo(string Stage, string Name, string Description, bool Implemented, IReadOnlyList<string> Profiles);

public sealed record StageInfo(int Number, string Name, string When, IReadOnlyList<TechniqueInfo> Techniques);

/// <summary>
/// The six-stage technique map shown in the UI: which technique belongs to which stage,
/// and which document profiles switch it on. Derived from <see cref="PipelineOptions.ForProfile"/>.
/// </summary>
public static class PipelineCatalog
{
    private static readonly (string Stage, string Name, string Description, bool Implemented, Func<PipelineOptions, bool> On)[] Techniques =
    [
        (Stages.Ingest, "Reading order for columns", "Left column before right, full-width bands in between", true, o => o.ColumnReadingOrder),
        (Stages.Ingest, "Ligature and hyphen repair", "Re-join words split across lines, fix ligature characters", true, o => o.RepairLigatures),
        (Stages.Ingest, "Label/value pairing", "Stat panels: pair each label with the value beside it", true, o => o.PairLabelValues),
        (Stages.Ingest, "Headings from fonts", "Bold or larger text becomes the section of each chunk", true, o => o.HeadingsFromFonts),
        (Stages.Ingest, "Chapters from bookmarks, boxes as streams", "PDF outline gives chapters; boxed articles kept apart; contents pages skipped", true, o => o.ParentChild),
        (Stages.Ingest, "Table rebuild", "Rows and columns restored as Markdown tables", true, o => o.TableRebuild),
        (Stages.Ingest, "Footnote links", "Footnote text travels with the sentence that cites it", true, o => o.FootnoteLinks),
        (Stages.Ingest, "Vision descriptions", "A vision model describes what each chart shows", true, o => o.VisionDescriptions),
        (Stages.Chunk, "Whole sentences + overlap", "Never cut mid-sentence; repeat the last sentences in the next chunk", true, o => o.SentenceChunks),
        (Stages.Chunk, "Heading path embedded", "Chapter > Section prefix is embedded with each chunk", true, o => o.HeadingPathEmbedded),
        (Stages.Chunk, "Parents and children", "Search small chunks, keep a link to the full section", true, o => o.ParentChild),
        (Stages.Chunk, "Chapter summaries", "One LLM-written summary per chapter for broad questions", true, o => o.ChapterSummaries),
        (Stages.Chunk, "One chunk per element", "Each table and each page of figures is its own chunk", true, o => o.TableRebuild || o.VisionDescriptions),
        (Stages.Query, "Rule-based routing", "Map words in the question to a chapter or region filter", true, o => o.Query == QueryMode.Rules),
        (Stages.Query, "LLM query understanding", "An LLM picks the sections from the outline (set Routing:Mode = Llm)", true, o => o.Query == QueryMode.Llm),
        (Stages.Retrieve, "Hybrid: dense + BM25", "Meaning (vectors) and exact words (keywords)", true, o => o.Hybrid),
        (Stages.Retrieve, "Reciprocal rank fusion", "Merge both result lists by rank", true, o => o.Hybrid),
        (Stages.Retrieve, "Metadata filter", "Search only the matching chapter or section", true, o => o.MetadataFilter),
        (Stages.PostProcess, "Cross-encoder rerank", "Reread the top 20 together with the question", true, o => o.Rerank),
        (Stages.PostProcess, "Small-to-big (parents)", "Send the parent section to the LLM", true, o => o.SmallToBig),
        (Stages.Generate, "Grounded prompt, citations, refusal", "Answer only from numbered passages, or refuse", true, _ => true),
    ];

    private static readonly (string Name, string When)[] StageOrder =
    [
        (Stages.Ingest, "Offline"), (Stages.Chunk, "Offline"), (Stages.Query, "Per question"),
        (Stages.Retrieve, "Per question"), (Stages.PostProcess, "Per question"), (Stages.Generate, "Per question"),
    ];

    public static IReadOnlyList<StageInfo> Build()
    {
        var profiles = Enum.GetValues<DocumentProfile>().Select(p => (p, PipelineOptions.ForProfile(p))).ToList();
        return StageOrder.Select((s, i) => new StageInfo(i + 1, s.Name, s.When,
            Techniques.Where(t => t.Stage == s.Name).Select(t => new TechniqueInfo(t.Stage, t.Name, t.Description, t.Implemented,
                profiles.Where(p => t.On(p.Item2)).Select(p => p.p.ToString()).ToList())).ToList())).ToList();
    }
}
