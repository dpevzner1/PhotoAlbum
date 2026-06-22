using PhotoAlbum.Core.Domain;

namespace PhotoAlbum.Core.Interfaces;

public record MediaFilter(
    string? SearchText = null,
    MediaType? MediaType = null,
    int? MinRating = null,
    bool? IsFavorite = null,
    bool? IsHidden = null,
    bool IncludeDeleted = false,
    bool OnlyDeleted = false,
    DateTime? CapturedAfter = null,
    DateTime? CapturedBefore = null,
    IReadOnlyList<long>? TagIds = null,
    IReadOnlyList<long>? PersonIds = null,
    IReadOnlyList<long>? PlaceIds = null,
    IReadOnlyList<long>? EventIds = null,
    IReadOnlyList<long>? AlbumIds = null,
    IReadOnlyList<int>? Years = null,
    int Page = 0,
    int PageSize = 100);

public interface IMediaItemRepository
{
    Task<MediaItem?> GetByIdAsync(long id, CancellationToken ct = default);
    Task<MediaItem?> GetByHashAsync(string blake3Hash, CancellationToken ct = default);
    Task<IReadOnlyList<MediaItem>> QueryAsync(MediaFilter filter, CancellationToken ct = default);
    Task<long> CountAsync(MediaFilter filter, CancellationToken ct = default);
    Task<long> InsertAsync(MediaItem item, CancellationToken ct = default);
    Task UpdateAsync(MediaItem item, CancellationToken ct = default);
    Task SoftDeleteAsync(long id, DeleteMode mode, CancellationToken ct = default);
    Task RestoreAsync(long id, CancellationToken ct = default);
    Task HardDeleteAsync(long id, CancellationToken ct = default);
    Task SetThumbnailPathAsync(long id, string path, CancellationToken ct = default);
    Task InsertFileLocationAsync(long mediaItemId, string filePath, long sizeBytes, long indexedFolderId, CancellationToken ct = default);

    Task SetRotationAsync(long id, int degrees, CancellationToken ct = default);
    Task<string?> GetPrimaryFilePathAsync(long id, CancellationToken ct = default);
    Task SetPHashAsync(long id, ulong pHash, CancellationToken ct = default);
    Task<IReadOnlyList<(long Id, string? FilePath)>> GetItemsMissingPHashAsync(int batchSize = 50, CancellationToken ct = default);
    Task<IReadOnlyList<(long Id, ulong PHash)>> GetAllPHashesAsync(CancellationToken ct = default);

    /// <summary>Returns (MediaItemId, primary FilePath) for every non-deleted item — used for bulk export.</summary>
    Task<IReadOnlyList<(long Id, string FilePath)>> GetAllFilePathsAsync(CancellationToken ct = default);
}
