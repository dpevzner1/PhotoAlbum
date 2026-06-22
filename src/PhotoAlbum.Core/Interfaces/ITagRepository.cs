using PhotoAlbum.Core.Domain;

namespace PhotoAlbum.Core.Interfaces;

public interface ITagRepository
{
    Task<Tag?> GetByIdAsync(long id, CancellationToken ct = default);
    Task<IReadOnlyList<Tag>> GetAllAsync(CancellationToken ct = default);
    Task<long> InsertAsync(Tag tag, CancellationToken ct = default);
    Task UpdateAsync(Tag tag, CancellationToken ct = default);
    Task DeleteAsync(long id, CancellationToken ct = default);
    Task TagMediaAsync(long mediaItemId, long tagId, CancellationToken ct = default);
    Task UntagMediaAsync(long mediaItemId, long tagId, CancellationToken ct = default);
    Task<IReadOnlyList<Tag>> GetTagsForMediaAsync(long mediaItemId, CancellationToken ct = default);
}
