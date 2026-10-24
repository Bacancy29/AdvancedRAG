using Dapper;
using Npgsql;
using NpgsqlTypes;
using Pgvector;
using Rag.Core;

namespace Rag.Data.Postgres;

public sealed class PgDocumentRepository(PgDatabase db) : IDocumentRepository
{
    private const string Columns =
        "id, name, file_path, profile, status, page_count, chunk_count, embedding_model, error, created_at, ingested_at";

    public async Task<IReadOnlyList<RagDocument>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<Row>($"SELECT {Columns} FROM documents ORDER BY id")).Select(r => r.ToModel()).ToList();
    }

    public async Task<RagDocument?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QuerySingleOrDefaultAsync<Row>($"SELECT {Columns} FROM documents WHERE id = @id", new { id }))?.ToModel();
    }

    public async Task<RagDocument?> FindByPathAsync(string filePath, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryFirstOrDefaultAsync<Row>($"SELECT {Columns} FROM documents WHERE file_path = @filePath", new { filePath }))?.ToModel();
    }

    public async Task<int> CreateAsync(RagDocument d, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(
            "INSERT INTO documents (name, file_path, profile, status) VALUES (@Name, @FilePath, @Profile, @Status) RETURNING id",
            new { d.Name, d.FilePath, Profile = d.Profile.ToString(), Status = d.Status.ToString() });
    }

    public async Task UpdateAsync(RagDocument d, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("""
            UPDATE documents SET name = @Name, profile = @Profile, status = @Status, page_count = @PageCount,
                chunk_count = @ChunkCount, embedding_model = @EmbeddingModel, error = @Error, ingested_at = @IngestedAt
            WHERE id = @Id
            """, new
        {
            d.Id, d.Name, Profile = d.Profile.ToString(), Status = d.Status.ToString(), d.PageCount, d.ChunkCount,
            d.EmbeddingModel, d.Error,
            IngestedAt = d.IngestedAt is { } t ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : (DateTime?)null,
        });
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("DELETE FROM documents WHERE id = @id", new { id });
    }

    private sealed class Row
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string Profile { get; set; } = "";
        public string Status { get; set; } = "";
        public int PageCount { get; set; }
        public int ChunkCount { get; set; }
        public string? EmbeddingModel { get; set; }
        public string? Error { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? IngestedAt { get; set; }

        public RagDocument ToModel() => new()
        {
            Id = Id, Name = Name, FilePath = FilePath,
            Profile = Enum.Parse<DocumentProfile>(Profile), Status = Enum.Parse<DocumentStatus>(Status),
            PageCount = PageCount, ChunkCount = ChunkCount, EmbeddingModel = EmbeddingModel, Error = Error,
            CreatedAt = CreatedAt, IngestedAt = IngestedAt,
        };
    }
}

public sealed class PgChunkRepository(PgDatabase db) : IChunkRepository
{
    public async Task ReplaceAsync(int documentId, string chunkSet, IReadOnlyList<Chunk> chunks, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync("DELETE FROM chunks WHERE document_id = @documentId AND chunk_set = @chunkSet", new { documentId, chunkSet }, tx);

        // Binary COPY: PostgreSQL's bulk-load path (the equivalent of SqlBulkCopy). Vectors are sent as pgvector values.
        await using (var writer = await conn.BeginBinaryImportAsync("""
            COPY chunks (id, document_id, chunk_set, parent_id, chunk_index, content_type, page_number, page_end,
                         section_path, text, token_count, embedding) FROM STDIN (FORMAT BINARY)
            """, ct))
        {
            foreach (var c in chunks)
            {
                await writer.StartRowAsync(ct);
                await writer.WriteAsync(c.Id, NpgsqlDbType.Uuid, ct);
                await writer.WriteAsync(documentId, NpgsqlDbType.Integer, ct);
                await writer.WriteAsync(chunkSet, NpgsqlDbType.Text, ct);
                if (c.ParentId is { } parent) await writer.WriteAsync(parent, NpgsqlDbType.Uuid, ct);
                else await writer.WriteNullAsync(ct);
                await writer.WriteAsync(c.ChunkIndex, NpgsqlDbType.Integer, ct);
                await writer.WriteAsync(c.ContentType, NpgsqlDbType.Text, ct);
                await writer.WriteAsync(c.PageNumber, NpgsqlDbType.Integer, ct);
                await writer.WriteAsync(c.PageEnd, NpgsqlDbType.Integer, ct);
                await writer.WriteAsync(c.SectionPath, NpgsqlDbType.Text, ct);
                await writer.WriteAsync(c.Text, NpgsqlDbType.Text, ct);
                await writer.WriteAsync(c.TokenCount, NpgsqlDbType.Integer, ct);
                if (c.Embedding.Length == 0) await writer.WriteNullAsync(ct);
                else await writer.WriteAsync(new Vector(c.Embedding), ct);
            }
            await writer.CompleteAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<Chunk>> GetAsync(int documentId, string chunkSet, bool includeEmbeddings, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        var embedding = includeEmbeddings ? "embedding" : "NULL::vector AS embedding";
        var rows = await conn.QueryAsync<Row>($"""
            SELECT id, document_id, chunk_set, parent_id, chunk_index, content_type, page_number, page_end,
                   section_path, text, token_count, {embedding}
            FROM chunks WHERE document_id = @documentId AND chunk_set = @chunkSet ORDER BY chunk_index
            """, new { documentId, chunkSet });

        return rows.Select(r => new Chunk
        {
            Id = r.Id, DocumentId = r.DocumentId, ChunkSet = r.ChunkSet, ParentId = r.ParentId, ChunkIndex = r.ChunkIndex,
            ContentType = r.ContentType, PageNumber = r.PageNumber, PageEnd = r.PageEnd, SectionPath = r.SectionPath,
            Text = r.Text, TokenCount = r.TokenCount, Embedding = r.Embedding?.ToArray() ?? [],
        }).ToList();
    }

    private sealed class Row
    {
        public Guid Id { get; set; }
        public int DocumentId { get; set; }
        public string ChunkSet { get; set; } = "";
        public Guid? ParentId { get; set; }
        public int ChunkIndex { get; set; }
        public string ContentType { get; set; } = "";
        public int PageNumber { get; set; }
        public int PageEnd { get; set; }
        public string SectionPath { get; set; } = "";
        public string Text { get; set; } = "";
        public int TokenCount { get; set; }
        public Vector? Embedding { get; set; }
    }
}

/// <summary>
/// Dense search inside PostgreSQL. `&lt;=&gt;` is pgvector's cosine-distance operator (0 = identical),
/// so similarity = 1 - distance. With the HNSW index this is an approximate search.
/// </summary>
public sealed class PgVectorDenseSearch(PgDatabase db) : IDenseSearch
{
    public string Name => "pgvector HNSW in PostgreSQL";
    public bool NeedsVectorsInMemory => false;

    public async Task<IReadOnlyList<(Guid ChunkId, double Score)>> SearchAsync(
        int documentId, string chunkSet, float[] query, int top, IReadOnlyList<string>? sectionPrefixes = null, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        // ef_search: candidates HNSW keeps while searching (higher = better recall, slower).
        // iterative_scan: keep scanning the index when the WHERE filter removes results (pgvector 0.8+).
        await conn.ExecuteAsync("SET LOCAL hnsw.ef_search = 100; SET LOCAL hnsw.iterative_scan = relaxed_order;", transaction: tx);

        // Metadata filter from query routing: vector search and the section filter run in one SQL query.
        var sectionFilter = sectionPrefixes is { Count: > 0 }
            ? "AND EXISTS (SELECT 1 FROM unnest(@prefixes) AS p WHERE starts_with(section_path, p))"
            : "";
        var rows = await conn.QueryAsync<(Guid Id, double Score)>($"""
            SELECT id, 1 - (embedding <=> @query) AS score
            FROM chunks
            WHERE document_id = @documentId AND chunk_set = @chunkSet AND embedding IS NOT NULL {sectionFilter}
            ORDER BY embedding <=> @query
            LIMIT @top
            """, new { query = new Vector(query), documentId, chunkSet, top, prefixes = sectionPrefixes?.ToArray() }, tx);

        await tx.CommitAsync(ct);
        // relaxed_order may return results slightly out of order; sort by the exact score.
        return rows.OrderByDescending(r => r.Score).Select(r => (r.Id, r.Score)).ToList();
    }
}

public sealed class PgMessageRepository(PgDatabase db) : IMessageRepository
{
    public async Task<IReadOnlyList<StoredMessage>> ListAsync(int documentId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<StoredMessage>(
            "SELECT id, document_id AS documentid, role, content, meta_json AS metajson, created_at AS createdat FROM messages WHERE document_id = @documentId ORDER BY id",
            new { documentId })).ToList();
    }

    public async Task AddAsync(int documentId, string role, string content, string? metaJson, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("INSERT INTO messages (document_id, role, content, meta_json) VALUES (@documentId, @role, @content, @metaJson)",
            new { documentId, role, content, metaJson });
    }

    public async Task ClearAsync(int documentId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("DELETE FROM messages WHERE document_id = @documentId", new { documentId });
    }
}

public sealed class PgEvalRepository(PgDatabase db) : IEvalRepository
{
    public async Task<long> AddAsync(int documentId, string resultJson, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long>(
            "INSERT INTO eval_runs (document_id, result_json) VALUES (@documentId, @resultJson) RETURNING id", new { documentId, resultJson });
    }

    public async Task<EvalRunRecord?> LatestAsync(int documentId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<EvalRunRecord>(
            "SELECT id, document_id AS documentid, created_at AS createdat, result_json AS resultjson FROM eval_runs WHERE document_id = @documentId ORDER BY id DESC LIMIT 1",
            new { documentId });
    }
}
