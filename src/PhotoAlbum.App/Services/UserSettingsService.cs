using PhotoAlbum.Core.Interfaces;

namespace PhotoAlbum.App.Services;

public sealed class UserSettingsService
{
    private readonly IUserSettingsRepository _repo;

    public UserSettingsService(IUserSettingsRepository repo) => _repo = repo;

    public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
        _repo.GetAsync(key, ct);

    public Task SetAsync(string key, string value, CancellationToken ct = default) =>
        _repo.SetAsync(key, value, ct);

    public async Task<string> GetOrDefaultAsync(string key, string defaultValue, CancellationToken ct = default)
    {
        var v = await _repo.GetAsync(key, ct);
        return v ?? defaultValue;
    }

    // Well-known keys
    public Task<string> GetThemeAsync(CancellationToken ct = default) =>
        GetOrDefaultAsync("ui.theme", "Dark", ct);

    public Task SetThemeAsync(string theme, CancellationToken ct = default) =>
        _repo.SetAsync("ui.theme", theme, ct);

    public async Task<bool> GetExifWriteBackEnabledAsync(CancellationToken ct = default) =>
        (await GetOrDefaultAsync("exif.writeback", "false", ct)) == "true";

    public Task SetExifWriteBackEnabledAsync(bool enabled, CancellationToken ct = default) =>
        _repo.SetAsync("exif.writeback", enabled ? "true" : "false", ct);
}
