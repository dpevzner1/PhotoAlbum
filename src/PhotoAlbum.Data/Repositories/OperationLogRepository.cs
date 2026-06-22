using Dapper;
using PhotoAlbum.Core.Interfaces;

namespace PhotoAlbum.Data.Repositories;

public sealed class OperationLogRepository : IOperationLogRepository
{
    private readonly DatabaseContext _db;
    public OperationLogRepository(DatabaseContext db) => _db = db;

    public async Task<long> LogAsync(string operationType, string entityType, long? entityId,
        string? payload, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long>("""
            INSERT INTO OperationLog(OperationType, EntityType, EntityId, Payload)
            VALUES(@operationType, @entityType, @entityId, @payload);
            SELECT last_insert_rowid();
            """, new { operationType, entityType, entityId, payload });
    }

    public async Task MarkUndoneAsync(long operationId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync("""
            UPDATE OperationLog SET IsUndone=1, UndoneUtc=datetime('now')
            WHERE Id=@operationId
            """, new { operationId });
    }

    public async Task<IReadOnlyList<OperationLogEntry>> GetRecentAsync(int count = 50, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<OperationLogRow>("""
            SELECT * FROM OperationLog ORDER BY OccurredUtc DESC LIMIT @count
            """, new { count });
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<OperationLogEntry?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<OperationLogRow>(
            "SELECT * FROM OperationLog WHERE Id=@id", new { id });
        return row?.ToDomain();
    }

    private sealed class OperationLogRow
    {
        public long Id { get; set; }
        public string OperationType { get; set; } = "";
        public string EntityType { get; set; } = "";
        public long? EntityId { get; set; }
        public string? Payload { get; set; }
        public string OccurredUtc { get; set; } = "";
        public int IsUndone { get; set; }
        public string? UndoneUtc { get; set; }

        public OperationLogEntry ToDomain() => new(
            Id, OperationType, EntityType, EntityId, Payload,
            DateTime.TryParse(OccurredUtc, out var d) ? d.ToUniversalTime() : DateTime.UtcNow,
            IsUndone != 0,
            UndoneUtc is null ? null : DateTime.TryParse(UndoneUtc, out var u) ? u.ToUniversalTime() : null);
    }
}
