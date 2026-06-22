using Dapper;
using PhotoAlbum.Core.Domain;
using PhotoAlbum.Core.Interfaces;

namespace PhotoAlbum.Data.Repositories;

public sealed class AlbumRepository : IAlbumRepository
{
    private readonly DatabaseContext _db;
    public AlbumRepository(DatabaseContext db) => _db = db;

    public async Task<Album?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Album>(
            "SELECT * FROM Album WHERE Id=@id", new { id });
    }

    public async Task<IReadOnlyList<Album>> GetAllAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<Album>("SELECT * FROM Album ORDER BY SortOrder, Name");
        return rows.ToList();
    }

    public async Task<long> InsertAsync(Album album, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long>("""
            INSERT INTO Album(Name, Description, CoverItemId, CreatedUtc, SortOrder, IsSmartAlbum, SmartQuery)
            VALUES(@Name, @Description, @CoverItemId, @CreatedUtc, @SortOrder, @IsSmartAlbum, @SmartQuery);
            SELECT last_insert_rowid();
            """, album);
    }

    public async Task UpdateAsync(Album album, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync("""
            UPDATE Album SET Name=@Name, Description=@Description, CoverItemId=@CoverItemId,
                SortOrder=@SortOrder, IsSmartAlbum=@IsSmartAlbum, SmartQuery=@SmartQuery
            WHERE Id=@Id
            """, album);
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync("DELETE FROM Album WHERE Id=@id", new { id });
    }

    public async Task AddMediaAsync(long albumId, long mediaItemId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync("""
            INSERT OR IGNORE INTO AlbumMedia(AlbumId, MediaItemId)
            VALUES(@albumId, @mediaItemId)
            """, new { albumId, mediaItemId });
    }

    public async Task RemoveMediaAsync(long albumId, long mediaItemId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(
            "DELETE FROM AlbumMedia WHERE AlbumId=@albumId AND MediaItemId=@mediaItemId",
            new { albumId, mediaItemId });
    }

    public async Task<IReadOnlyList<long>> GetMediaIdsAsync(long albumId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var ids = await conn.QueryAsync<long>(
            "SELECT MediaItemId FROM AlbumMedia WHERE AlbumId=@albumId ORDER BY Position",
            new { albumId });
        return ids.ToList();
    }

    public async Task<IReadOnlyList<Album>> GetAlbumsForMediaAsync(long mediaItemId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return (await conn.QueryAsync<Album>("""
            SELECT a.* FROM Album a
            JOIN AlbumMedia am ON am.AlbumId = a.Id
            WHERE am.MediaItemId = @mediaItemId
            ORDER BY a.Name
            """, new { mediaItemId })).ToList();
    }
}
