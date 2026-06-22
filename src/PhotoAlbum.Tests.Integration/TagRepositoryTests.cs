using PhotoAlbum.Core.Domain;
using PhotoAlbum.Core.Interfaces;
using PhotoAlbum.Data.Repositories;

namespace PhotoAlbum.Tests.Integration;

public sealed class TagRepositoryTests : IClassFixture<DatabaseFixture>
{
    private readonly ITagRepository _tags;
    private readonly IMediaItemRepository _media;

    public TagRepositoryTests(DatabaseFixture fixture)
    {
        _tags = new TagRepository(fixture.Db);
        _media = new MediaItemRepository(fixture.Db);
    }

    [Fact]
    public async Task Create_tag_appears_in_list()
    {
        var id = await _tags.InsertAsync(new Tag { Name = "nature", Color = "#2ECC71" });
        var all = await _tags.GetAllAsync();
        Assert.Contains(all, t => t.Id == id && t.Name == "nature");
    }

    [Fact]
    public async Task Tag_and_untag_media()
    {
        var tagId = await _tags.InsertAsync(new Tag { Name = $"tag_{Guid.NewGuid():N}" });
        var mediaId = await _media.InsertAsync(new MediaItem
        {
            Blake3Hash = $"tg_{Guid.NewGuid():N}",
            OriginalName = "tagged.jpg",
            MediaType = MediaType.Photo,
        });

        await _tags.TagMediaAsync(mediaId, tagId);
        var tagged = await _tags.GetTagsForMediaAsync(mediaId);
        Assert.Contains(tagged, t => t.Id == tagId);

        await _tags.UntagMediaAsync(mediaId, tagId);
        tagged = await _tags.GetTagsForMediaAsync(mediaId);
        Assert.DoesNotContain(tagged, t => t.Id == tagId);
    }

    [Fact]
    public async Task Duplicate_tag_idempotent()
    {
        var tagId = await _tags.InsertAsync(new Tag { Name = $"dup_{Guid.NewGuid():N}" });
        var mediaId = await _media.InsertAsync(new MediaItem
        {
            Blake3Hash = $"dup2_{Guid.NewGuid():N}",
            OriginalName = "dup.jpg",
            MediaType = MediaType.Photo,
        });

        await _tags.TagMediaAsync(mediaId, tagId);
        await _tags.TagMediaAsync(mediaId, tagId); // should not throw
        var tagged = await _tags.GetTagsForMediaAsync(mediaId);
        Assert.Single(tagged, t => t.Id == tagId);
    }
}
