using Dapper;
using PhotoAlbum.Data;
using PhotoAlbum.Data.Migrations;
using System.IO;

namespace PhotoAlbum.Tests.Unit;

public sealed class MigrationRunnerTests : IAsyncLifetime, IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseContext _db;

    public MigrationRunnerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"pa_unit_{Guid.NewGuid():N}.db");
        _db = new DatabaseContext(_dbPath);
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task RunAsync_creates_all_tables()
    {
        await _db.EnsureInitializedAsync();
        await using var conn = await _db.OpenAsync();

        var tables = (await conn.QueryAsync<string>(
            "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name")).ToList();

        // Verify core schema tables exist
        Assert.Contains("MediaItem", tables);
        Assert.Contains("Album", tables);
        Assert.Contains("Tag", tables);
        Assert.Contains("Person", tables);
        Assert.Contains("Place", tables);
        Assert.Contains("Event", tables);
        Assert.Contains("UserSettings", tables);
        Assert.Contains("OperationLog", tables);
        Assert.Contains("BinaryManifest", tables);
        Assert.Contains("SchemaVersion", tables);
    }

    [Fact]
    public async Task RunAsync_is_idempotent()
    {
        await _db.EnsureInitializedAsync();
        // Running twice must not throw
        var runner = new MigrationRunner(GetConnectionString());
        await runner.RunAsync();
        await runner.RunAsync();
    }

    [Fact]
    public async Task SchemaVersion_records_migration()
    {
        await _db.EnsureInitializedAsync();
        await using var conn = await _db.OpenAsync();
        var versions = (await conn.QueryAsync<int>("SELECT Version FROM SchemaVersion")).ToList();
        Assert.Contains(1, versions);
    }

    private string GetConnectionString()
        => new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
            Cache = Microsoft.Data.Sqlite.SqliteCacheMode.Shared,
        }.ConnectionString;
}
