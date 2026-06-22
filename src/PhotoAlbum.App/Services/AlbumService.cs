using PhotoAlbum.Core.Domain;
using PhotoAlbum.Core.Interfaces;

namespace PhotoAlbum.App.Services;

public sealed class AlbumService
{
    private readonly IAlbumRepository _albums;
    private readonly IOperationLogRepository _opLog;

    public AlbumService(IAlbumRepository albums, IOperationLogRepository opLog)
    {
        _albums = albums;
        _opLog = opLog;
    }

    public Task<IReadOnlyList<Album>> GetAllAsync(CancellationToken ct = default) =>
        _albums.GetAllAsync(ct);

    public async Task<Album> CreateAsync(string name, CancellationToken ct = default)
    {
        var album = new Album { Name = name };
        var id = await _albums.InsertAsync(album, ct);
        await _opLog.LogAsync("Create", "Album", id, name, ct);
        return new Album { Id = id, Name = name };
    }

    public async Task RenameAsync(long albumId, string newName, CancellationToken ct = default)
    {
        var album = await _albums.GetByIdAsync(albumId, ct)
            ?? throw new KeyNotFoundException($"Album {albumId} not found");
        album.Name = newName;
        await _albums.UpdateAsync(album, ct);
        await _opLog.LogAsync("Rename", "Album", albumId, newName, ct);
    }

    public async Task DeleteAsync(long albumId, CancellationToken ct = default)
    {
        await _albums.DeleteAsync(albumId, ct);
        await _opLog.LogAsync("Delete", "Album", albumId, null, ct);
    }

    public Task AddMediaAsync(long albumId, long mediaItemId, CancellationToken ct = default) =>
        _albums.AddMediaAsync(albumId, mediaItemId, ct);

    public Task RemoveMediaAsync(long albumId, long mediaItemId, CancellationToken ct = default) =>
        _albums.RemoveMediaAsync(albumId, mediaItemId, ct);

    public Task<IReadOnlyList<long>> GetMediaIdsAsync(long albumId, CancellationToken ct = default) =>
        _albums.GetMediaIdsAsync(albumId, ct);
}
