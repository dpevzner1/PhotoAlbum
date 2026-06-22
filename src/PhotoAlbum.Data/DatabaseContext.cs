using Dapper;
using Microsoft.Data.Sqlite;
using PhotoAlbum.Data.Migrations;

namespace PhotoAlbum.Data;

/// <summary>
/// Owns the SQLite connection string, runs migrations on first open,
/// and provides open connections to repositories.
/// </summary>
public sealed class DatabaseContext : IAsyncDisposable
{
    private readonly string _connectionString;
    private bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public DatabaseContext(string dbPath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        };
        _connectionString = builder.ConnectionString;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
    }

    public async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        if (_initialized) return;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;
            var runner = new MigrationRunner(_connectionString);
            await runner.RunAsync(ct);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync("PRAGMA foreign_keys=ON;");
        return conn;
    }

    public async ValueTask DisposeAsync()
    {
        _initLock.Dispose();
        await Task.CompletedTask;
    }
}
