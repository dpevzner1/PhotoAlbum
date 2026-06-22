using PhotoAlbum.Core.Domain;

namespace PhotoAlbum.Core.Interfaces;

public interface IPlaceRepository
{
    Task<Place?> GetByIdAsync(long id, CancellationToken ct = default);
    Task<IReadOnlyList<Place>> GetAllAsync(CancellationToken ct = default);
    Task<long> InsertAsync(Place place, CancellationToken ct = default);
    Task UpdateAsync(Place place, CancellationToken ct = default);
    Task DeleteAsync(long id, CancellationToken ct = default);
    Task AssignMediaAsync(long mediaItemId, long placeId, CancellationToken ct = default);
    Task UnassignMediaAsync(long mediaItemId, long placeId, CancellationToken ct = default);
    Task<IReadOnlyList<Place>> GetPlacesForMediaAsync(long mediaItemId, CancellationToken ct = default);
    Task<IReadOnlyList<long>> GetMediaIdsAsync(long placeId, CancellationToken ct = default);
}
