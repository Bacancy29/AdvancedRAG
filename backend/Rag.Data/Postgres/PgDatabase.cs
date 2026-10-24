using System.Reflection;
using Dapper;
using Npgsql;
using Pgvector.Dapper;
using Pgvector.Npgsql;
using Rag.Core;

namespace Rag.Data.Postgres;

/// <summary>Shared Npgsql data source with the pgvector type registered.</summary>
public sealed class PgDatabase : IAsyncDisposable
{
    static PgDatabase()
    {
        // snake_case columns (document_id) map to C# properties (DocumentId).
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new VectorTypeHandler());
    }

    public PgDatabase(string connectionString, int dimensions)
    {
        ConnectionString = connectionString;
        Dimensions = dimensions;
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseVector();
        DataSource = builder.Build();
    }

    public string ConnectionString { get; }
    public int Dimensions { get; }
    public NpgsqlDataSource DataSource { get; }

    public ValueTask<NpgsqlConnection> OpenAsync(CancellationToken ct = default) => DataSource.OpenConnectionAsync(ct);

    public ValueTask DisposeAsync() => DataSource.DisposeAsync();
}

/// <summary>Creates the database if needed, enables pgvector and applies the schema.</summary>
public sealed class PgDbInitializer(PgDatabase db) : IDatabaseInitializer
{
    public string Provider => "PostgreSQL + pgvector";

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var target = new NpgsqlConnectionStringBuilder(db.ConnectionString);
        var database = target.Database ?? "advancedrag";
        var admin = new NpgsqlConnectionStringBuilder(db.ConnectionString) { Database = "postgres" };

        await using (var conn = new NpgsqlConnection(admin.ConnectionString))
        {
            await conn.OpenAsync(ct);
            var exists = await conn.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname = @database)", new { database });
            if (!exists) await conn.ExecuteAsync($"CREATE DATABASE \"{database.Replace("\"", "\"\"")}\"");
        }

        await using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Rag.Data.Postgres.postgres-schema.sql")
            ?? throw new InvalidOperationException("postgres-schema.sql resource not found");
        var sql = (await new StreamReader(stream).ReadToEndAsync(ct)).Replace("{DIMENSIONS}", db.Dimensions.ToString());

        try
        {
            await using var c = await db.OpenAsync(ct);
            await c.ExecuteAsync(sql);
            await c.ReloadTypesAsync();   // let Npgsql learn the "vector" type created by the extension
        }
        catch (PostgresException ex) when (ex.SqlState == "0A000" || ex.Message.Contains("vector"))
        {
            throw new InvalidOperationException(
                "pgvector is not installed in PostgreSQL. Run D:\\AI\\tools\\install-pgvector.ps1 as Administrator. " + ex.Message, ex);
        }
    }
}
