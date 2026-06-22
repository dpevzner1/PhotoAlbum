using Dapper;
using PhotoAlbum.Core.Domain;
using PhotoAlbum.Core.Interfaces;

namespace PhotoAlbum.Data.Repositories;

public sealed class PersonRepository : IPersonRepository
{
    private readonly DatabaseContext _db;
    public PersonRepository(DatabaseContext db) => _db = db;

    public async Task<Person?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Person>("SELECT * FROM Person WHERE Id=@id", new { id });
    }

    public async Task<IReadOnlyList<Person>> GetAllAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return (await conn.QueryAsync<Person>("SELECT * FROM Person ORDER BY Name")).ToList();
    }

    public async Task<long> InsertAsync(Person person, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long>("""
            INSERT INTO Person(Name, Notes, AvatarPath, CreatedUtc)
            VALUES(@Name, @Notes, @AvatarPath, @CreatedUtc);
            SELECT last_insert_rowid();
            """, person);
    }

    public async Task UpdateAsync(Person person, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE Person SET Name=@Name, Notes=@Notes, AvatarPath=@AvatarPath WHERE Id=@Id",
            person);
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync("DELETE FROM Person WHERE Id=@id", new { id });
    }

    public async Task TagMediaAsync(long mediaItemId, long personId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync("""
            INSERT OR IGNORE INTO MediaPerson(MediaItemId, PersonId) VALUES(@mediaItemId, @personId)
            """, new { mediaItemId, personId });
    }

    public async Task UntagMediaAsync(long mediaItemId, long personId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(
            "DELETE FROM MediaPerson WHERE MediaItemId=@mediaItemId AND PersonId=@personId",
            new { mediaItemId, personId });
    }

    public async Task<IReadOnlyList<Person>> GetPeopleForMediaAsync(long mediaItemId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var people = await conn.QueryAsync<Person>("""
            SELECT p.* FROM Person p
            JOIN MediaPerson mp ON mp.PersonId=p.Id
            WHERE mp.MediaItemId=@mediaItemId
            ORDER BY p.Name
            """, new { mediaItemId });
        return people.ToList();
    }

    public async Task<IReadOnlyList<long>> GetMediaIdsAsync(long personId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var ids = await conn.QueryAsync<long>(
            "SELECT MediaItemId FROM MediaPerson WHERE PersonId=@personId", new { personId });
        return ids.ToList();
    }
}
