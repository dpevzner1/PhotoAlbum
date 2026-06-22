using Dapper;
using PhotoAlbum.Core.Domain;
using PhotoAlbum.Core.Interfaces;

namespace PhotoAlbum.Data.Repositories;

public sealed class EventRepository : IEventRepository
{
    private readonly DatabaseContext _db;
    public EventRepository(DatabaseContext db) => _db = db;

    public async Task<Event?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Event>("SELECT * FROM Event WHERE Id=@id", new { id });
    }

    public async Task<IReadOnlyList<Event>> GetAllAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return (await conn.QueryAsync<Event>("SELECT * FROM Event ORDER BY StartUtc DESC")).ToList();
    }

    public async Task<long> InsertAsync(Event ev, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long>("""
            INSERT INTO Event(Name, StartUtc, EndUtc, Description, CreatedUtc)
            VALUES(@Name, @StartUtc, @EndUtc, @Description, @CreatedUtc);
            SELECT last_insert_rowid();
            """, ev);
    }

    public async Task UpdateAsync(Event ev, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE Event SET Name=@Name, StartUtc=@StartUtc, EndUtc=@EndUtc, Description=@Description WHERE Id=@Id",
            ev);
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync("DELETE FROM Event WHERE Id=@id", new { id });
    }

    public async Task AssignMediaAsync(long mediaItemId, long eventId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync("""
            INSERT OR IGNORE INTO MediaEvent(MediaItemId, EventId) VALUES(@mediaItemId, @eventId)
            """, new { mediaItemId, eventId });
    }

    public async Task UnassignMediaAsync(long mediaItemId, long eventId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(
            "DELETE FROM MediaEvent WHERE MediaItemId=@mediaItemId AND EventId=@eventId",
            new { mediaItemId, eventId });
    }

    public async Task<IReadOnlyList<long>> GetMediaIdsAsync(long eventId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return (await conn.QueryAsync<long>(
            "SELECT MediaItemId FROM MediaEvent WHERE EventId=@eventId ORDER BY MediaItemId",
            new { eventId })).ToList();
    }
}
