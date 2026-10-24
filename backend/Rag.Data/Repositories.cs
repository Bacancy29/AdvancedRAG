using System.Data;
using System.Runtime.InteropServices;
using Dapper;
using Microsoft.Data.SqlClient;
using Rag.Core;

namespace Rag.Data;

public sealed class DocumentRepository(SqlConnectionFactory db) : IDocumentRepository
{
    private const string Columns =
        "Id, Name, FilePath, Profile, Status, PageCount, ChunkCount, EmbeddingModel, Error, CreatedAt, IngestedAt";

    public async Task<IReadOnlyList<RagDocument>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<DocumentRow>($"SELECT {Columns} FROM dbo.Documents ORDER BY Id");
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task<RagDocument?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<DocumentRow>(
            $"SELECT {Columns} FROM dbo.Documents WHERE Id = @id", new { id });
        return row?.ToModel();
    }

    public async Task<RagDocument?> FindByPathAsync(string filePath, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<DocumentRow>(
            $"SELECT {Columns} FROM dbo.Documents WHERE FilePath = @filePath", new { filePath });
        return row?.ToModel();
    }

    public async Task<int> CreateAsync(RagDocument d, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>("""
            INSERT INTO dbo.Documents (Name, FilePath, Profile, Status)
            OUTPUT INSERTED.Id
            VALUES (@Name, @FilePath, @Profile, @Status)
            """, new { d.Name, d.FilePath, Profile = d.Profile.ToString(), Status = d.Status.ToString() });
    }

    public async Task UpdateAsync(RagDocument d, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("""
            UPDATE dbo.Documents SET
                Name = @Name, Profile = @Profile, Status = @Status, PageCount = @PageCount,
                ChunkCount = @ChunkCount, EmbeddingModel = @EmbeddingModel, Error = @Error, IngestedAt = @IngestedAt
            WHERE Id = @Id
            """, new
        {
            d.Id, d.Name, Profile = d.Profile.ToString(), Status = d.Status.ToString(), d.PageCount,
            d.ChunkCount, d.EmbeddingModel, d.Error, d.IngestedAt,
        });
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("DELETE FROM dbo.Documents WHERE Id = @id", new { id });
    }

    private sealed class DocumentRow
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
            PageCount = PageCount, ChunkCount = ChunkCount, EmbeddingModel = EmbeddingModel,
            Error = Error, CreatedAt = CreatedAt, IngestedAt = IngestedAt,
        };
    }
}

public sealed class ChunkRepository(SqlConnectionFactory db) : IChunkRepository
{
    public async Task ReplaceAsync(int documentId, string chunkSet, IReadOnlyList<Chunk> chunks, CancellationToken ct = default)
    {
        try
        {
            await ReplaceOnceAsync(documentId, chunkSet, chunks, ct);
        }
        catch (SqlException ex) when (ex.Number == -2)
        {
            // Timeout on a stale pooled connection (ingestion idles for minutes while embedding): retry on a fresh one.
            SqlConnection.ClearAllPools();
            await ReplaceOnceAsync(documentId, chunkSet, chunks, ct);
        }
    }

    private async Task ReplaceOnceAsync(int documentId, string chunkSet, IReadOnlyList<Chunk> chunks, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync("DELETE FROM dbo.Chunks WHERE DocumentId = @documentId AND ChunkSet = @chunkSet",
            new { documentId, chunkSet }, tx);

        // SqlBulkCopy: one round-trip instead of one INSERT per chunk.
        var table = new DataTable();
        table.Columns.Add("Id", typeof(Guid));
        table.Columns.Add("DocumentId", typeof(int));
        table.Columns.Add("ChunkSet", typeof(string));
        table.Columns.Add("ParentId", typeof(Guid));
        table.Columns.Add("ChunkIndex", typeof(int));
        table.Columns.Add("ContentType", typeof(string));
        table.Columns.Add("PageNumber", typeof(int));
        table.Columns.Add("PageEnd", typeof(int));
        table.Columns.Add("SectionPath", typeof(string));
        table.Columns.Add("Text", typeof(string));
        table.Columns.Add("TokenCount", typeof(int));
        table.Columns.Add("Embedding", typeof(byte[]));

        foreach (var c in chunks)
        {
            table.Rows.Add(c.Id, documentId, chunkSet, (object?)c.ParentId ?? DBNull.Value, c.ChunkIndex,
                c.ContentType, c.PageNumber, c.PageEnd, Truncate(c.SectionPath, 500), c.Text, c.TokenCount,
                c.Embedding.Length == 0 ? DBNull.Value : EmbeddingCodec.ToBytes(c.Embedding));
        }

        using var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, tx) { DestinationTableName = "dbo.Chunks", BulkCopyTimeout = 300 };
        foreach (DataColumn col in table.Columns) bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        await bulk.WriteToServerAsync(table, ct);

        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<Chunk>> GetAsync(int documentId, string chunkSet, bool includeEmbeddings, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        var embedding = includeEmbeddings ? "Embedding" : "CAST(NULL AS VARBINARY(MAX)) AS Embedding";
        var rows = await conn.QueryAsync<ChunkRow>($"""
            SELECT Id, DocumentId, ChunkSet, ParentId, ChunkIndex, ContentType, PageNumber, PageEnd,
                   SectionPath, Text, TokenCount, {embedding}
            FROM dbo.Chunks WHERE DocumentId = @documentId AND ChunkSet = @chunkSet
            ORDER BY ChunkIndex
            """, new { documentId, chunkSet });

        return rows.Select(r => new Chunk
        {
            Id = r.Id, DocumentId = r.DocumentId, ChunkSet = r.ChunkSet, ParentId = r.ParentId,
            ChunkIndex = r.ChunkIndex, ContentType = r.ContentType, PageNumber = r.PageNumber,
            PageEnd = r.PageEnd, SectionPath = r.SectionPath, Text = r.Text, TokenCount = r.TokenCount,
            Embedding = r.Embedding is null ? [] : EmbeddingCodec.FromBytes(r.Embedding),
        }).ToList();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private sealed class ChunkRow
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
        public byte[]? Embedding { get; set; }
    }
}

public static class EmbeddingCodec
{
    public static byte[] ToBytes(float[] vector) => MemoryMarshal.AsBytes(vector.AsSpan()).ToArray();

    public static float[] FromBytes(byte[] bytes) => MemoryMarshal.Cast<byte, float>(bytes).ToArray();
}

public sealed class MessageRepository(SqlConnectionFactory db) : IMessageRepository
{
    public async Task<IReadOnlyList<StoredMessage>> ListAsync(int documentId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<StoredMessage>(
            "SELECT Id, DocumentId, Role, Content, MetaJson, CreatedAt FROM dbo.Messages WHERE DocumentId = @documentId ORDER BY Id",
            new { documentId });
        return rows.ToList();
    }

    public async Task AddAsync(int documentId, string role, string content, string? metaJson, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO dbo.Messages (DocumentId, Role, Content, MetaJson) VALUES (@documentId, @role, @content, @metaJson)",
            new { documentId, role, content, metaJson });
    }

    public async Task ClearAsync(int documentId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("DELETE FROM dbo.Messages WHERE DocumentId = @documentId", new { documentId });
    }
}

public sealed class EvalRepository(SqlConnectionFactory db) : IEvalRepository
{
    public async Task<long> AddAsync(int documentId, string resultJson, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long>(
            "INSERT INTO dbo.EvalRuns (DocumentId, ResultJson) OUTPUT INSERTED.Id VALUES (@documentId, @resultJson)",
            new { documentId, resultJson });
    }

    public async Task<EvalRunRecord?> LatestAsync(int documentId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<EvalRunRecord>(
            "SELECT TOP 1 Id, DocumentId, CreatedAt, ResultJson FROM dbo.EvalRuns WHERE DocumentId = @documentId ORDER BY Id DESC",
            new { documentId });
    }
}
