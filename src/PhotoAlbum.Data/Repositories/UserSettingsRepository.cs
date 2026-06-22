using System.Text.Json;
using Dapper;
using PhotoAlbum.Core.Interfaces;

namespace PhotoAlbum.Data.Repositories;

public sealed class UserSettingsRepository : IUserSettingsRepository
{
    private readonly DatabaseContext _db;
    public UserSettingsRepository(DatabaseContext db) => _db = db;

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT Value FROM UserSettings WHERE Key=@key", new { key });
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default) where T : class
    {
        var raw = await GetAsync(key, ct);
        if (raw is null) return null;
        return JsonSerializer.Deserialize<T>(raw);
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO UserSettings(Key, Value) VALUES(@key, @value)
            ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value, UpdatedUtc=datetime('now')
            """, new { key, value });
    }

    public async Task SetAsync<T>(string key, T value, CancellationToken ct = default) where T : class
    {
        await SetAsync(key, JsonSerializer.Serialize(value), ct);
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync("DELETE FROM UserSettings WHERE Key=@key", new { key });
    }
}
