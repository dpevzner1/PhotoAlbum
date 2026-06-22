using PhotoAlbum.Core.Domain;
using PhotoAlbum.Core.Interfaces;

namespace PhotoAlbum.App.Services;

public sealed class TagService
{
    private readonly ITagRepository _tags;
    private readonly IOperationLogRepository _opLog;

    public TagService(ITagRepository tags, IOperationLogRepository opLog)
    {
        _tags = tags;
        _opLog = opLog;
    }

    public Task<IReadOnlyList<Tag>> GetAllTagsAsync(CancellationToken ct = default) =>
        _tags.GetAllAsync(ct);

    public async Task<Tag> CreateTagAsync(string name, string? color = null, CancellationToken ct = default)
    {
        var tag = new Tag { Name = name, Color = color };
        var id = await _tags.InsertAsync(tag, ct);
        await _opLog.LogAsync("Create", "Tag", id, name, ct);
        return new Tag { Id = id, Name = name, Color = color };
    }

    public async Task ApplyTagAsync(long mediaItemId, long tagId, CancellationToken ct = default)
    {
        await _tags.TagMediaAsync(mediaItemId, tagId, ct);
        await _opLog.LogAsync("Tag", "MediaItem", mediaItemId, tagId.ToString(), ct);
    }

    public async Task RemoveTagAsync(long mediaItemId, long tagId, CancellationToken ct = default)
    {
        await _tags.UntagMediaAsync(mediaItemId, tagId, ct);
        await _opLog.LogAsync("Untag", "MediaItem", mediaItemId, tagId.ToString(), ct);
    }

    public Task<IReadOnlyList<Tag>> GetTagsForMediaAsync(long mediaItemId, CancellationToken ct = default) =>
        _tags.GetTagsForMediaAsync(mediaItemId, ct);
}
