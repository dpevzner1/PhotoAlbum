using Dapper;
using Microsoft.Data.Sqlite;

namespace PhotoAlbum.Data.Migrations;

/// <summary>
/// Runs versioned SQLite migrations exactly once each, tracking applied versions
/// in a SchemaVersion table.  Thread-safe via WAL + exclusive BEGIN IMMEDIATE.
/// </summary>
public sealed class MigrationRunner
{
    private readonly string _connectionString;

    public MigrationRunner(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        // WAL mode for better concurrent read performance
        await conn.ExecuteAsync("PRAGMA journal_mode=WAL;");
        await conn.ExecuteAsync("PRAGMA foreign_keys=ON;");

        // Bootstrap version tracking
        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS SchemaVersion (
                Version    INTEGER PRIMARY KEY,
                AppliedUtc TEXT NOT NULL DEFAULT(datetime('now'))
            );
            """);

        var applied = (await conn.QueryAsync<int>("SELECT Version FROM SchemaVersion"))
            .ToHashSet();

        await ApplyIfNeeded(conn, applied, Migration001_InitialSchema.Version,
            Migration001_InitialSchema.Sql, ct);
        await ApplyIfNeeded(conn, applied, Migration002_IndexedFolders.Version,
            Migration002_IndexedFolders.Sql, ct);
        await ApplyIfNeeded(conn, applied, Migration003_PHash.Version,
            Migration003_PHash.Sql, ct);
        await ApplyIfNeeded(conn, applied, Migration004_GpsCoords.Version,
            Migration004_GpsCoords.Sql, ct);
        await ApplyIfNeeded(conn, applied, Migration005_Rotation.Version,
            Migration005_Rotation.Sql, ct);
    }

    private static async Task ApplyIfNeeded(SqliteConnection conn, HashSet<int> applied,
        int version, string sql, CancellationToken ct)
    {
        if (applied.Contains(version))
            return;

        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await conn.ExecuteAsync(sql, transaction: (SqliteTransaction)tx);
            await conn.ExecuteAsync(
                "INSERT INTO SchemaVersion(Version) VALUES (@v)",
                new { v = version },
                transaction: (SqliteTransaction)tx);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }
}
