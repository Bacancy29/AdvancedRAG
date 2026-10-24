using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rag.Core;
using Rag.Generation;
using Rag.Hosting;
using Rag.Ingestion.Chunking;
using Rag.Ingestion.Layout;
using Rag.Ingestion.Footnotes;
using Rag.Ingestion.Strategies;

// Command-line tool for the same pipeline the API uses:
//   init                       create database + register documents
//   preview <pdf>              show chunks without embedding or saving
//   ingest [docId]             parse, chunk, embed and store (default: all documents)
//   ask "<question>" [--doc N] [--naive]
//   eval [--doc N] [--no-answers]

var command = args.FirstOrDefault() ?? "help";

if (command == "preview" && args.Length >= 2)
{
    var layout = new PdfLayoutReader().Read(args[1], new LayoutOptions(true, true, true, true));
    Console.WriteLine(layout.Stats);
    foreach (var c in new SentenceChunker(new SentenceChunkerOptions()).Chunk(layout.Segments, 0))
        Console.WriteLine($"#{c.ChunkIndex} p{c.PageNumber}-{c.PageEnd} [{c.ContentType}] {{{c.SectionPath}}} ({c.TokenCount}t)\n{c.Text}\n");
    return 0;
}

if (command == "preview-long" && args.Length >= 2)
{
    var outline = DocumentOutline.Read(args[1]);
    Console.WriteLine($"Chapters: {string.Join(" | ", outline.Chapters.Select(c => $"p{c.Page} {c.Title}"))}");
    Console.WriteLine($"Skipped contents pages: {string.Join(",", outline.ContentsPages.Order())}");
    var layout = new PdfLayoutReader().Read(args[1], new LayoutOptions(true, true, true, Structured: true, SkipPages: outline.ContentsPages));
    Console.WriteLine(layout.Stats);
    var result = new ParentChildChunker(new ParentChildOptions()).Chunk(layout.Segments, outline, 0);
    Console.WriteLine($"Children: {result.Children.Count}, parents: {result.Parents.Count}");
    foreach (var c in result.Children)
        Console.WriteLine($"#{c.ChunkIndex} p{c.PageNumber}-{c.PageEnd} [{c.ContentType}] {{{c.SectionPath}}} ({c.TokenCount}t)\n{c.Text}\n");
    return 0;
}

if (command == "preview-visual" && args.Length >= 2)
{
    var layout = new PdfLayoutReader().Read(args[1], new LayoutOptions(true, true, true, Structured: true, DetectTables: true, FootnoteMarkers: true));
    Console.WriteLine(layout.Stats);
    var chunks = new SentenceChunker(new SentenceChunkerOptions()).Chunk(layout.Segments, 0);
    var start = layout.Segments.ToList().FindIndex(s => s.Kind == SegmentKind.Heading && FootnoteLinker.IsEndnoteSection(s.Text));
    var notes = FootnoteLinker.ReadEndnotes(start < 0 ? "" : string.Join(" ", layout.Segments.Skip(start + 1)
        .Where(s => s.Kind is SegmentKind.Body or SegmentKind.Aside).Select(s => s.Text)));
    Console.WriteLine($"Endnotes: {notes.Count}; links: {FootnoteLinker.Link(chunks, notes)}; figure pages: {string.Join(",", VisualStrategy.FigurePages(args[1]))}");
    foreach (var c in chunks)
        Console.WriteLine($"#{c.ChunkIndex} p{c.PageNumber}-{c.PageEnd} [{c.ContentType}] {{{c.SectionPath}}} ({c.TokenCount}t)\n{c.Text}\n");
    return 0;
}

var builder = Host.CreateApplicationBuilder();
builder.Configuration.Sources.Clear();
builder.Configuration
    .AddJsonFile(AppPaths.Resolve("Rag.Api/appsettings.json"), optional: false)
    .AddUserSecrets("advanced-rag")
    .AddEnvironmentVariables();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.AddAdvancedRag(builder.Configuration);
using var host = builder.Build();
var sp = host.Services;

await sp.GetRequiredService<StartupTasks>().RunAsync();
var documents = sp.GetRequiredService<IDocumentRepository>();
int DocArg() => args.SkipWhile(a => a != "--doc").Skip(1).Select(int.Parse).FirstOrDefault(1);

switch (command)
{
    case "init":
        foreach (var d in await documents.ListAsync())
            Console.WriteLine($"{d.Id}: {d.Name} [{d.Profile}] {d.Status} — {d.FilePath}");
        break;

    case "ingest":
    {
        var ids = args.Length > 1 ? [int.Parse(args[1])] : (await documents.ListAsync()).Select(d => d.Id).ToArray();
        var service = sp.GetRequiredService<Rag.Ingestion.IngestionService>();
        foreach (var id in ids)
        {
            var last = "";
            var output = await service.IngestAsync(id, new Progress<IngestProgress>(p =>
            {
                if (p.Stage == last && p.Percent % 10 != 0) return;
                last = p.Stage;
                Console.WriteLine($"  [{p.Percent,3}%] {p.Stage}: {p.Message}");
            }), CancellationToken.None);
            foreach (var t in output.Trace) Console.WriteLine($"  {t.Stage} — {t.Technique}: {t.Summary}");
        }
        break;
    }

    case "ask" when args.Length > 1:
    {
        var rag = sp.GetRequiredService<RagService>();
        await foreach (var e in rag.AskStreamAsync(DocArg(), args[1], naive: args.Contains("--naive"), saveHistory: false))
        {
            if (e is TokenEvent t) Console.Write(t.Text);
            if (e is CompletedEvent done)
            {
                Console.WriteLine("\n");
                foreach (var s in done.Answer.Trace)
                    Console.WriteLine($"  {(s.Enabled ? "●" : "○")} {s.Stage,-12} {s.ElapsedMs,6} ms  {s.Technique}");
                Console.WriteLine();
                foreach (var c in done.Answer.Citations)
                    Console.WriteLine($"  [{c.Number}]{(c.Cited ? "*" : " ")} p.{c.Page} score {c.Score:0.###} {{{c.Section}}} {Short(c.Text)}");
            }
        }
        break;
    }

    case "eval":
    {
        var eval = sp.GetRequiredService<EvaluationService>();
        var report = await eval.RunAsync(DocArg(), includeAnswers: !args.Contains("--no-answers"), new Progress<string>(m => Console.WriteLine($"  … {m}")), resume: args.Contains("--resume"));
        Console.WriteLine($"\nRetrieval ladder ({report.EmbeddingModel}; {report.VectorSearch}), MRR and Hit@5:");
        foreach (var s in report.Ladder)
            Console.WriteLine($"  {s.Name,-36} MRR {s.Mrr:0.000}  Hit@5 {s.HitAt5:P0}{(s.SectionHit is { } sh ? $"  right section {sh:P0}" : "")}  ranks [{string.Join(", ", s.Ranks.Select(r => r?.ToString() ?? "-"))}]");
        if (report.Answers.Count > 0)
        {
            Console.WriteLine($"\nAnswers ({report.ChatModel}):");
            foreach (var a in report.Answers)
                Console.WriteLine($"  {(a.Passed ? "PASS" : "FAIL")} {a.Id,-4} {a.Question}\n         → {a.Error ?? a.Answer}");
            Console.WriteLine($"\n{report.Passed}/{report.Total} passed. Required questions: {(report.RequiredPassed ? "ALL PASS" : "NOT ALL PASSED")}");
        }
        break;
    }

    default:
        Console.WriteLine("Commands: init | preview <pdf> | ingest [docId] | ask \"<question>\" [--doc N] [--naive] | eval [--doc N] [--no-answers] [--resume]");
        break;
}
return 0;

static string Short(string s) => s.Length > 110 ? s[..110] + "…" : s;
