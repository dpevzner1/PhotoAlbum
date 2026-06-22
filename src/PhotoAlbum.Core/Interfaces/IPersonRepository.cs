using PhotoAlbum.Core.Domain;

namespace PhotoAlbum.Core.Interfaces;

public interface IPersonRepository
{
    Task<Person?> GetByIdAsync(long id, CancellationToken ct = default);
    Task<IReadOnlyList<Person>> GetAllAsync(CancellationToken ct = default);
    Task<long> InsertAsync(Person person, CancellationToken ct = default);
    Task UpdateAsync(Person person, CancellationToken ct = default);
    Task DeleteAsync(long id, CancellationToken ct = default);
    Task TagMediaAsync(long mediaItemId, long personId, CancellationToken ct = default);
    Task UntagMediaAsync(long mediaItemId, long personId, CancellationToken ct = default);
    Task<IReadOnlyList<Person>> GetPeopleForMediaAsync(long mediaItemId, CancellationToken ct = default);
    Task<IReadOnlyList<long>> GetMediaIdsAsync(long personId, CancellationToken ct = default);
}
