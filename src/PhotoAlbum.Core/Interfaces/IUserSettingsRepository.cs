namespace PhotoAlbum.Core.Interfaces;

public interface IUserSettingsRepository
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default) where T : class;
    Task SetAsync(string key, string value, CancellationToken ct = default);
    Task SetAsync<T>(string key, T value, CancellationToken ct = default) where T : class;
    Task DeleteAsync(string key, CancellationToken ct = default);
}
