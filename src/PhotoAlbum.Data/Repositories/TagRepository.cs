using Dapper;
using PhotoAlbum.Core.Domain;
using PhotoAlbum.Core.Interfaces;

namespace PhotoAlbum.Data.Repositories;

public sealed class TagRepository : ITagRepository
{
    private readonly DatabaseContext _db;
    public TagRepository(DatabaseContext db) => _db = db;

    public async Task<Tag?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Tag>("SELECT * FROM Tag WHERE Id=@id", new { id });
    }

    public async Task<IReadOnlyList<Tag>> GetAllAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return (await conn.QueryAsync<Tag>("SELECT * FROM Tag ORDER BY Name")).ToList();
    }

    public async Task<long> InsertAsync(Tag tag, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long>("""
            INSERT INTO Tag(Name, Color, ParentId) VALUES(@Name, @Color, @ParentId);
            SELECT last_insert_rowid();
            """, tag);
    }

    public async Task UpdateAsync(Tag tag, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE Tag SET Name=@Name, Color=@Color, ParentId=@ParentId WHERE Id=@Id",
            tag);
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync("DELETE FROM Tag WHERE Id=@id", new { id });
    }

    public async Task TagMediaAsync(long mediaItemId, long tagId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync("""
            INSERT OR IGNORE INTO MediaTag(MediaItemId, TagId) VALUES(@mediaItemId, @tagId)
            """, new { mediaItemId, tagId });
    }

    public async Task UntagMediaAsync(long mediaItemId, long tagId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(
            "DELETE FROM MediaTag WHERE MediaItemId=@mediaItemId AND TagId=@tagId",
            new { mediaItemId, tagId });
    }

    public async Task<IReadOnlyList<Tag>> GetTagsForMediaAsync(long mediaItemId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var tags = await conn.QueryAsync<Tag>("""
            SELECT t.* FROM Tag t
            JOIN MediaTag mt ON mt.TagId=t.Id
            WHERE mt.MediaItemId=@mediaItemId
            ORDER BY t.Name
            """, new { mediaItemId });
        return tags.ToList();
    }
}
