using PhotoAlbum.Core.Domain;

namespace PhotoAlbum.Core.Interfaces;

public interface IAlbumRepository
{
    Task<Album?> GetByIdAsync(long id, CancellationToken ct = default);
    Task<IReadOnlyList<Album>> GetAllAsync(CancellationToken ct = default);
    Task<long> InsertAsync(Album album, CancellationToken ct = default);
    Task UpdateAsync(Album album, CancellationToken ct = default);
    Task DeleteAsync(long id, CancellationToken ct = default);
    Task AddMediaAsync(long albumId, long mediaItemId, CancellationToken ct = default);
    Task RemoveMediaAsync(long albumId, long mediaItemId, CancellationToken ct = default);
    Task<IReadOnlyList<long>> GetMediaIdsAsync(long albumId, CancellationToken ct = default);
    Task<IReadOnlyList<Album>> GetAlbumsForMediaAsync(long mediaItemId, CancellationToken ct = default);
}
