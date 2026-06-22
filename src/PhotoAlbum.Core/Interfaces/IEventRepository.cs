using PhotoAlbum.Core.Domain;

namespace PhotoAlbum.Core.Interfaces;

public interface IEventRepository
{
    Task<Event?> GetByIdAsync(long id, CancellationToken ct = default);
    Task<IReadOnlyList<Event>> GetAllAsync(CancellationToken ct = default);
    Task<long> InsertAsync(Event ev, CancellationToken ct = default);
    Task UpdateAsync(Event ev, CancellationToken ct = default);
    Task DeleteAsync(long id, CancellationToken ct = default);
    Task AssignMediaAsync(long mediaItemId, long eventId, CancellationToken ct = default);
    Task UnassignMediaAsync(long mediaItemId, long eventId, CancellationToken ct = default);
    Task<IReadOnlyList<long>> GetMediaIdsAsync(long eventId, CancellationToken ct = default);
}
