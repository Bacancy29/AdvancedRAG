namespace Rag.Ingestion.Layout;

public enum SegmentKind
{
    /// <summary>A section title. Level 1 = standalone heading line, level 2 = run-in heading ("History From the ...").</summary>
    Heading,
    /// <summary>Main narrative flow. Consecutive body segments form continuous text, even across columns and pages.</summary>
    Body,
    /// <summary>Text outside the main flow: photo captions, callout boxes, stat panels.</summary>
    Aside,
    /// <summary>Page header of a boxed article ("BOX 1.1 How much will AI affect global growth?"); its pages form a separate stream.</summary>
    BoxMarker,
    /// <summary>A table rebuilt from cells aligned in rows and columns; Text is Markdown.</summary>
    Table,
}

public sealed record LayoutSegment(SegmentKind Kind, string Text, int Page, int HeadingLevel = 0);

public sealed record LayoutStats(int Pages, int Blocks, int DroppedMargin, int DroppedOffPage, int Headings, int Asides, int DroppedFragments = 0, int SkippedPages = 0, int Tables = 0, int FootnoteMarkers = 0);

public sealed record DocumentLayout(IReadOnlyList<LayoutSegment> Segments, LayoutStats Stats);
