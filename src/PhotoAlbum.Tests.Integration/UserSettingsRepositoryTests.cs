using PhotoAlbum.Data.Repositories;

namespace PhotoAlbum.Tests.Integration;

public sealed class UserSettingsRepositoryTests : IClassFixture<DatabaseFixture>
{
    private readonly UserSettingsRepository _repo;

    public UserSettingsRepositoryTests(DatabaseFixture fixture)
    {
        _repo = new UserSettingsRepository(fixture.Db);
    }

    [Fact]
    public async Task Set_and_get_string_value()
    {
        await _repo.SetAsync("test.key", "hello");
        var v = await _repo.GetAsync("test.key");
        Assert.Equal("hello", v);
    }

    [Fact]
    public async Task Get_missing_key_returns_null()
    {
        var v = await _repo.GetAsync("no.such.key");
        Assert.Null(v);
    }

    [Fact]
    public async Task Overwrite_value()
    {
        await _repo.SetAsync("over.key", "first");
        await _repo.SetAsync("over.key", "second");
        var v = await _repo.GetAsync("over.key");
        Assert.Equal("second", v);
    }

    [Fact]
    public async Task Delete_key()
    {
        await _repo.SetAsync("del.key", "value");
        await _repo.DeleteAsync("del.key");
        var v = await _repo.GetAsync("del.key");
        Assert.Null(v);
    }

    [Fact]
    public async Task Set_and_get_object()
    {
        var obj = new TestObj("Alice", 42);
        await _repo.SetAsync("obj.key", obj);
        var loaded = await _repo.GetAsync<TestObj>("obj.key");
        Assert.NotNull(loaded);
        Assert.Equal("Alice", loaded!.Name);
        Assert.Equal(42, loaded.Value);
    }

    private record TestObj(string Name, int Value);
}
