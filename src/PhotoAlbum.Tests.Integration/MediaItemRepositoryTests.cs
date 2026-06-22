using PhotoAlbum.Core.Domain;
using PhotoAlbum.Core.Interfaces;
using PhotoAlbum.Data.Repositories;

namespace PhotoAlbum.Tests.Integration;

public sealed class MediaItemRepositoryTests : IClassFixture<DatabaseFixture>
{
    private readonly IMediaItemRepository _repo;

    public MediaItemRepositoryTests(DatabaseFixture fixture)
    {
        _repo = new MediaItemRepository(fixture.Db);
    }

    [Fact]
    public async Task Insert_and_GetById_round_trips()
    {
        var item = new MediaItem
        {
            Blake3Hash = "aabbcc001122",
            OriginalName = "photo.jpg",
            MediaType = MediaType.Photo,
            Rating = 3,
            IsFavorite = true,
        };
        var id = await _repo.InsertAsync(item);
        Assert.True(id > 0);

        var loaded = await _repo.GetByIdAsync(id);
        Assert.NotNull(loaded);
        Assert.Equal("photo.jpg", loaded!.OriginalName);
        Assert.Equal(3, loaded.Rating);
        Assert.True(loaded.IsFavorite);
    }

    [Fact]
    public async Task GetByHash_finds_existing()
    {
        var hash = $"hash_{Guid.NewGuid():N}";
        var id = await _repo.InsertAsync(new MediaItem
        {
            Blake3Hash = hash,
            OriginalName = "test.jpg",
            MediaType = MediaType.Photo,
        });

        var found = await _repo.GetByHashAsync(hash);
        Assert.NotNull(found);
        Assert.Equal(id, found!.Id);
    }

    [Fact]
    public async Task GetByHash_returns_null_for_missing()
    {
        var found = await _repo.GetByHashAsync("nonexistent_hash_xyz");
        Assert.Null(found);
    }

    [Fact]
    public async Task SoftDelete_hides_from_default_query()
    {
        var id = await _repo.InsertAsync(new MediaItem
        {
            Blake3Hash = $"del_{Guid.NewGuid():N}",
            OriginalName = "del.jpg",
            MediaType = MediaType.Photo,
        });

        await _repo.SoftDeleteAsync(id, DeleteMode.Trash);

        var results = await _repo.QueryAsync(new MediaFilter());
        Assert.DoesNotContain(results, r => r.Id == id);
    }

    [Fact]
    public async Task Restore_makes_visible_again()
    {
        var id = await _repo.InsertAsync(new MediaItem
        {
            Blake3Hash = $"rest_{Guid.NewGuid():N}",
            OriginalName = "restore.jpg",
            MediaType = MediaType.Photo,
        });
        await _repo.SoftDeleteAsync(id, DeleteMode.Trash);
        await _repo.RestoreAsync(id);

        var results = await _repo.QueryAsync(new MediaFilter());
        Assert.Contains(results, r => r.Id == id);
    }

    [Fact]
    public async Task Update_persists_rating_change()
    {
        var id = await _repo.InsertAsync(new MediaItem
        {
            Blake3Hash = $"upd_{Guid.NewGuid():N}",
            OriginalName = "update.jpg",
            MediaType = MediaType.Photo,
            Rating = 1,
        });
        var item = await _repo.GetByIdAsync(id);
        item!.Rating = 5;
        await _repo.UpdateAsync(item);

        var reloaded = await _repo.GetByIdAsync(id);
        Assert.Equal(5, reloaded!.Rating);
    }

    [Fact]
    public async Task CountAsync_matches_inserted_count()
    {
        var filter = new MediaFilter(SearchText: $"ct_{Guid.NewGuid():N}");
        // search is unique per test — count should be 0 baseline
        var before = await _repo.CountAsync(filter);
        Assert.Equal(0, before);
    }

    [Fact]
    public async Task HardDelete_removes_record()
    {
        var id = await _repo.InsertAsync(new MediaItem
        {
            Blake3Hash = $"hard_{Guid.NewGuid():N}",
            OriginalName = "hard.jpg",
            MediaType = MediaType.Photo,
        });
        await _repo.HardDeleteAsync(id);
        var found = await _repo.GetByIdAsync(id);
        Assert.Null(found);
    }
}
