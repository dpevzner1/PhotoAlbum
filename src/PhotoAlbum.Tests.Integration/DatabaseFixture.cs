using PhotoAlbum.Data;
using System.IO;

namespace PhotoAlbum.Tests.Integration;

/// <summary>Creates a fresh in-memory SQLite DB for each test class.</summary>
public sealed class DatabaseFixture : IAsyncLifetime, IDisposable
{
    private readonly string _dbPath;
    public DatabaseContext Db { get; }

    public DatabaseFixture()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"pa_test_{Guid.NewGuid():N}.db");
        Db = new DatabaseContext(_dbPath);
    }

    public async Task InitializeAsync() => await Db.EnsureInitializedAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
    }
}
