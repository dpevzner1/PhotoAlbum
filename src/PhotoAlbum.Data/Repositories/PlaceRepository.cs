using Dapper;
using PhotoAlbum.Core.Domain;
using PhotoAlbum.Core.Interfaces;

namespace PhotoAlbum.Data.Repositories;

public sealed class PlaceRepository : IPlaceRepository
{
    private readonly DatabaseContext _db;
    public PlaceRepository(DatabaseContext db) => _db = db;

    public async Task<Place?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Place>("SELECT * FROM Place WHERE Id=@id", new { id });
    }

    public async Task<IReadOnlyList<Place>> GetAllAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return (await conn.QueryAsync<Place>("SELECT * FROM Place ORDER BY Name")).ToList();
    }

    public async Task<long> InsertAsync(Place place, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long>("""
            INSERT INTO Place(Name, Latitude, Longitude, Radius, CreatedUtc)
            VALUES(@Name, @Latitude, @Longitude, @Radius, @CreatedUtc);
            SELECT last_insert_rowid();
            """, place);
    }

    public async Task UpdateAsync(Place place, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE Place SET Name=@Name, Latitude=@Latitude, Longitude=@Longitude, Radius=@Radius WHERE Id=@Id",
            place);
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync("DELETE FROM Place WHERE Id=@id", new { id });
    }

    public async Task AssignMediaAsync(long mediaItemId, long placeId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync("""
            INSERT OR IGNORE INTO MediaPlace(MediaItemId, PlaceId) VALUES(@mediaItemId, @placeId)
            """, new { mediaItemId, placeId });
    }

    public async Task UnassignMediaAsync(long mediaItemId, long placeId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(
            "DELETE FROM MediaPlace WHERE MediaItemId=@mediaItemId AND PlaceId=@placeId",
            new { mediaItemId, placeId });
    }

    public async Task<IReadOnlyList<Place>> GetPlacesForMediaAsync(long mediaItemId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return (await conn.QueryAsync<Place>("""
            SELECT p.* FROM Place p
            JOIN MediaPlace mp ON mp.PlaceId = p.Id
            WHERE mp.MediaItemId = @mediaItemId
            ORDER BY p.Name
            """, new { mediaItemId })).ToList();
    }

    public async Task<IReadOnlyList<long>> GetMediaIdsAsync(long placeId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return (await conn.QueryAsync<long>(
            "SELECT MediaItemId FROM MediaPlace WHERE PlaceId=@placeId ORDER BY MediaItemId",
            new { placeId })).ToList();
    }
}
