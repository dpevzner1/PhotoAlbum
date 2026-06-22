namespace PhotoAlbum.Core.Interfaces;

public record OperationLogEntry(
    long Id,
    string OperationType,
    string EntityType,
    long? EntityId,
    string? Payload,
    DateTime OccurredUtc,
    bool IsUndone,
    DateTime? UndoneUtc);

public interface IOperationLogRepository
{
    Task<long> LogAsync(string operationType, string entityType, long? entityId, string? payload, CancellationToken ct = default);
    Task MarkUndoneAsync(long operationId, CancellationToken ct = default);
    Task<IReadOnlyList<OperationLogEntry>> GetRecentAsync(int count = 50, CancellationToken ct = default);
    Task<OperationLogEntry?> GetByIdAsync(long id, CancellationToken ct = default);
}
