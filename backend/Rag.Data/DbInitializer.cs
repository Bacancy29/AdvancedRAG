using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Rag.Core;

namespace Rag.Data;

/// <summary>Creates the database if it is missing, then applies the idempotent schema script.</summary>
public sealed class DbInitializer(SqlConnectionFactory factory) : IDatabaseInitializer
{
    public string Provider => "SQL Server";

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var builder = new SqlConnectionStringBuilder(factory.ConnectionString);
        var database = builder.InitialCatalog;

        builder.InitialCatalog = "master";
        await using (var master = new SqlConnection(builder.ConnectionString))
        {
            await master.OpenAsync(ct);
            await using var cmd = master.CreateCommand();
            // SQL Express creates databases with AUTO_CLOSE ON, which shuts the database down between
            // connections and breaks long-running work (e.g. saving after minutes of embedding).
            var quoted = database.Replace("]", "]]");
            cmd.CommandText = $"""
                IF DB_ID(@name) IS NULL EXEC('CREATE DATABASE [{quoted}]');
                IF DATABASEPROPERTYEX(@name, 'IsAutoClose') = 1 EXEC('ALTER DATABASE [{quoted}] SET AUTO_CLOSE OFF');
                """;
            cmd.Parameters.AddWithValue("@name", database);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using var conn = await factory.OpenAsync(ct);
        foreach (var batch in SchemaBatches())
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = batch;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static IEnumerable<string> SchemaBatches()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Rag.Data.Sql.schema.sql")
            ?? throw new InvalidOperationException("schema.sql resource not found");
        using var reader = new StreamReader(stream);
        return Regex.Split(reader.ReadToEnd(), @"^\s*GO\s*$", RegexOptions.Multiline)
            .Where(b => !string.IsNullOrWhiteSpace(b));
    }
}
