using PhotoAlbum.Core.Domain;
using PhotoAlbum.Core.Interfaces;
using PhotoAlbum.Data.Repositories;

namespace PhotoAlbum.Tests.Integration;

public sealed class AlbumRepositoryTests : IClassFixture<DatabaseFixture>
{
    private readonly IAlbumRepository _albums;
    private readonly IMediaItemRepository _media;

    public AlbumRepositoryTests(DatabaseFixture fixture)
    {
        _albums = new AlbumRepository(fixture.Db);
        _media = new MediaItemRepository(fixture.Db);
    }

    [Fact]
    public async Task Create_and_list_album()
    {
        var id = await _albums.InsertAsync(new Album { Name = "Summer 2024" });
        var all = await _albums.GetAllAsync();
        Assert.Contains(all, a => a.Id == id && a.Name == "Summer 2024");
    }

    [Fact]
    public async Task Add_and_remove_media()
    {
        var albumId = await _albums.InsertAsync(new Album { Name = "Test Album" });
        var mediaId = await _media.InsertAsync(new MediaItem
        {
            Blake3Hash = $"alb_{Guid.NewGuid():N}",
            OriginalName = "a.jpg",
            MediaType = MediaType.Photo,
        });

        await _albums.AddMediaAsync(albumId, mediaId);
        var ids = await _albums.GetMediaIdsAsync(albumId);
        Assert.Contains(mediaId, ids);

        await _albums.RemoveMediaAsync(albumId, mediaId);
        ids = await _albums.GetMediaIdsAsync(albumId);
        Assert.DoesNotContain(mediaId, ids);
    }

    [Fact]
    public async Task Delete_album_cascades_media_links()
    {
        var albumId = await _albums.InsertAsync(new Album { Name = "ToDelete" });
        await _albums.DeleteAsync(albumId);
        var found = await _albums.GetByIdAsync(albumId);
        Assert.Null(found);
    }
}
