# Advanced RAG — .NET 8 + React

Question answering over documents with very different structures. One pipeline, six stages;
each document **profile** switches techniques on or off.

| Use case | Document | Profile | Status |
|---|---|---|---|
| 1 | GPO 2018 Annual Report (narrative, two columns) | `PlainText` | ✅ built |
| 2 | World Bank Global Economic Prospects (~200 pages) | `LongReport` | ✅ built |
| 3 | Apple iMac Environmental Report (tables, charts, footnotes) | `Visual` | ✅ built |

## Pipeline (use case 1)

| # | Stage | Technique | Where |
|---|---|---|---|
| 1 | Ingest | Column reading order, header/footer removal, hyphen/ligature repair, headings from fonts, label/value pairing for stat panels | `Rag.Ingestion/Layout/PdfLayoutReader.cs` |
| 2 | Chunk | Whole sentences (~250 tokens) with 2-sentence overlap; captions kept out of the sentence flow | `Rag.Ingestion/Chunking/SentenceChunker.cs` |
| 3 | Query | — (not needed for one narrative document) | `Rag.Retrieval/RetrievalPipeline.cs` |
| 4 | Retrieve | Dense (nomic-embed-text) + BM25, merged with reciprocal rank fusion | `Rag.Retrieval/` |
| 5 | Post-process | Cross-encoder rerank (ms-marco-MiniLM, ONNX Runtime, local) | `Rag.Retrieval/CrossEncoderReranker.cs` |
| 6 | Generate | Numbered passages, cite `[n]`, copy facts exactly, or refuse | `Rag.Generation/AnswerGenerator.cs` |

## Pipeline (use case 2: long, structured report)

| # | Stage | Technique | Where |
|---|---|---|---|
| 1 | Ingest | Chapters from PDF bookmarks; sections/regions/sub-sections from font size and family; boxed articles as a separate stream (box text is 10.6 pt vs 11 pt); contents pages skipped; chart/table number fragments dropped | `Layout/DocumentOutline.cs`, `PdfLayoutReader` (`Structured` mode) |
| 2 | Chunk | Parents = whole sections (~600 tokens, stored, not searched); children = ~200-token sentence chunks (searched), embedded with their heading path; one LLM summary per chapter (cached) | `Chunking/ParentChildChunker.cs`, `ChapterSummarizer.cs` |
| 3 | Query | Rule-based routing: "Chapter 3", "executive summary", "Box 3.1", region names and aliases (`Routing:Aliases`) → section filter. `Routing:Mode = Llm` lets the chat model pick sections instead | `Rag.Retrieval/QueryRouter.cs` |
| 4 | Retrieve | Dense + BM25 + RRF **inside the routed sections** (pgvector: one SQL query with the filter); falls back to the whole document if the filter leaves < 3 hits | `RetrievalPipeline.cs` |
| 5 | Post-process | Rerank children (with heading path), then **small-to-big**: send each child's parent section to the LLM | `RetrievalPipeline.SmallToBigAsync` |
| 6 | Generate | Same grounded prompt | |

Retrieval ladder on the GEP report (12 questions): MRR 0.38 (naive) → 0.55 (structure-aware chunks) → 0.58 (+ hybrid) → 0.85 (+ rerank) → **0.89 (+ routing)**; top passage from the right chapter/region: **100%**.

Embeddings are cached in `backend/data/cache/embeddings` (keyed by text hash), so re-ingesting unchanged text is instant.

## Pipeline (use case 3: tables, charts, footnotes)

| # | Stage | Technique | Where |
|---|---|---|---|
| 1 | Ingest | **Tables**: small text cells aligned in rows (centres within 3 pt) and columns (left edges within 12 pt), mostly numeric → Markdown table | `PdfLayoutReader.ExtractTables` |
| 1 | Ingest | **Footnotes**: superscript numbers (≤ 75% of the surrounding size) → `[^n]`; endnote text read from the "Endnotes" pages and appended to every chunk that cites it | `PdfLayoutReader.MarkFootnotes`, `Footnotes/FootnoteLinker.cs` |
| 1 | Ingest | **Charts**: pages with drawings/images (not mostly small running text) rendered to PNG (PDFtoImage) and described by Gemini vision; cached by image hash | `Visuals/VisionDescriber.cs`, `VisualStrategy.FigurePages` |
| 2 | Chunk | One chunk per element: each table, each figure page; text by sentences with section from the 24 pt page titles | `Strategies/VisualStrategy.cs` |
| 4–6 | | Hybrid + rerank + grounded answer, as for the other documents | |

Rendered pages are served at `/api/documents/{id}/pages/{n}.png`; the UI shows table chunks as tables, figure chunks with their page image, and footnotes under the passage.

Every document is also indexed with a **naive** baseline (1,000-character chunks, dense only) so the
evaluation can show what each technique adds.

## Stack (all free)

- **Backend:** ASP.NET Core minimal API (.NET 8), PdfPig, Microsoft.Extensions.AI, OllamaSharp, ONNX Runtime, Dapper
- **Storage:** SQL Server 2022 Express (`AdvancedRag` database; vectors as `VARBINARY`, searched in memory)
- **Models:** Ollama `nomic-embed-text` (embeddings), Gemini free tier (answers; switchable to Ollama), cross-encoder reranker (local)
- **Frontend:** React + TypeScript + Vite, MUI, TanStack Query, React Router

## Setup

```powershell
# 1. Models
ollama pull nomic-embed-text
ollama pull llama3.2:3b            # optional local fallback for answers
# reranker: backend/models/ms-marco-MiniLM-L-6-v2/{model.onnx,vocab.txt}
#   from https://huggingface.co/Xenova/ms-marco-MiniLM-L-6-v2 (onnx/model.onnx, vocab.txt)

# 2. Gemini key (free: https://aistudio.google.com/apikey) — stored outside the repo
cd backend
dotnet user-secrets set "AI:Chat:ApiKey" "<your key>" --id advanced-rag

# 3. Database + ingestion (creates AdvancedRag on .\SQLEXPRESS)
dotnet run --project Rag.Cli -- init
dotnet run --project Rag.Cli -- ingest        # ~5 min on CPU (embedding)

# 4. Run
dotnet run --project Rag.Api                  # http://localhost:5080
cd ../frontend; npm install; npm run dev      # http://localhost:5173
```

Visual Studio 2022: open `backend/AdvancedRag.sln`, set **Rag.Api** as startup project, F5.

### Switching the database: SQL Server ↔ PostgreSQL + pgvector

| `Database:Provider` | Vectors stored as | Dense search |
|---|---|---|
| `SqlServer` (default) | `VARBINARY` bytes (SQL Server 2022 has no vector type) | exact cosine in C#, in memory |
| `Postgres` | native `vector(768)` column + HNSW index | `ORDER BY embedding <=> @query` inside PostgreSQL |

Everything else (BM25, RRF, reranker, LLM, UI) is identical, so evaluation results are directly comparable.

```powershell
# one time: install the locally built pgvector (Administrator PowerShell)
D:\AI\tools\install-pgvector.ps1
# password of the "postgres" user, kept out of appsettings
dotnet user-secrets set "Database:PostgresPassword" "<password>" --id advanced-rag
# switch: set "Database": { "Provider": "Postgres" } in Rag.Api/appsettings.json, then
dotnet run --project Rag.Cli -- init      # creates database "advancedrag", extension, tables, HNSW index
dotnet run --project Rag.Cli -- ingest
dotnet run --project Rag.Cli -- eval
```

### Switching the answer model

`backend/Rag.Api/appsettings.json` → `AI:Chat`:

```jsonc
{ "Provider": "Gemini", "Model": "gemini-3.5-flash" }   // default
{ "Provider": "Ollama", "Model": "llama3.2:3b" }       // fully local
{ "Provider": "OpenAI", "Model": "gpt-4o-mini", "Endpoint": "https://models.github.ai/inference" } // GitHub Models, Groq, …
```

Changing the **embedding** model requires re-ingesting (questions and chunks must use the same model).

## CLI

```powershell
dotnet run --project Rag.Cli -- preview ..\..\GPO-Anual-report.pdf   # chunks only, no DB
dotnet run --project Rag.Cli -- ask "How many depository libraries did GPO staff visit in FY 2018?"
dotnet run --project Rag.Cli -- ask "..." --naive                      # baseline for comparison
dotnet run --project Rag.Cli -- eval                                   # ladder + answers
dotnet run --project Rag.Cli -- eval --no-answers                      # retrieval only (no LLM calls)
```

## Evaluation set

`backend/eval/<pdf name>.json`: 3 required questions from the brief, 8 more facts (including
two from the "GPO by the Numbers" stat panel) and 3 trap questions whose only correct answer is a refusal.

## Adding use cases 2 and 3

1. Implement `IIngestionStrategy` for the profile (`LongReportStrategy`, `VisualStrategy`) and register it.
2. Switch on its techniques in `PipelineOptions.ForProfile` and mark them implemented in `PipelineCatalog`.
3. Add `eval/<pdf name>.json`. The API, database and UI need no changes: documents, source cards and the
   pipeline view are driven by profile, content type and stage data.
