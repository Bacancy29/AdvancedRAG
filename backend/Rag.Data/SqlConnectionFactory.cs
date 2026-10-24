using Microsoft.Data.SqlClient;

namespace Rag.Data;

public sealed class SqlConnectionFactory(string connectionString)
{
    public string ConnectionString { get; } = connectionString;

    public async Task<SqlConnection> OpenAsync(CancellationToken ct = default)
    {
        var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        return conn;
    }
}
