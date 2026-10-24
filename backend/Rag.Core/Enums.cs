namespace Rag.Core;

/// <summary>Document structure type. Each profile switches pipeline stages on or off.</summary>
public enum DocumentProfile
{
    PlainText,   // Use case 1: narrative text (GPO Annual Report)
    LongReport,  // Use case 2: long, hierarchical (World Bank)
    Visual       // Use case 3: tables, charts, footnotes (Apple iMac)
}

public enum DocumentStatus { NotIngested, Ingesting, Ready, Failed }

public enum QueryMode { None, Rules, Llm }

/// <summary>The six stages of the pipeline, in order.</summary>
public static class Stages
{
    public const string Ingest = "Ingest";
    public const string Chunk = "Chunk";
    public const string Query = "Query";
    public const string Retrieve = "Retrieve";
    public const string PostProcess = "Post-process";
    public const string Generate = "Generate";
}

/// <summary>Each document is indexed twice so naive and advanced pipelines can be compared.</summary>
public static class ChunkSets
{
    public const string Advanced = "advanced";
    public const string Naive = "naive";
    /// <summary>Full sections for small-to-big: stored, never searched.</summary>
    public const string Parent = "parent";
}

public static class ContentTypes
{
    public const string Text = "text";
    public const string Aside = "aside";      // captions, callout boxes, sidebars
    public const string Table = "table";
    public const string Chart = "chart";
    public const string Footnote = "footnote";
    public const string Summary = "summary";
}
