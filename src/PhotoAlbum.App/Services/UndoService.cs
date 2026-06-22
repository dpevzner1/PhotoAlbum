using Microsoft.Extensions.Logging;
using PhotoAlbum.Core.Domain;
using PhotoAlbum.Core.Interfaces;

namespace PhotoAlbum.App.Services;

/// <summary>
/// Undo the most recent undoable operation from the OperationLog.
/// Supports: Delete → Restore, Tag → Untag, BulkDelete → Restore all.
/// </summary>
public sealed class UndoService
{
    private readonly IOperationLogRepository _opLog;
    private readonly IMediaItemRepository _mediaRepo;
    private readonly ITagRepository _tagRepo;
    private readonly ILogger<UndoService> _log;

    public UndoService(
        IOperationLogRepository opLog,
        IMediaItemRepository mediaRepo,
        ITagRepository tagRepo,
        ILogger<UndoService> log)
    {
        _opLog = opLog;
        _mediaRepo = mediaRepo;
        _tagRepo = tagRepo;
        _log = log;
    }

    public async Task<bool> UndoLastAsync(CancellationToken ct = default)
    {
        var recent = await _opLog.GetRecentAsync(1, ct);
        var op = recent.FirstOrDefault();
        if (op is null || op.IsUndone)
        {
            _log.LogInformation("Nothing to undo");
            return false;
        }

        var success = op.OperationType switch
        {
            "Delete" or "BulkDelete" or "SoftDelete" => await UndoDeleteAsync(op, ct),
            "Tag"                                    => await UndoTagAsync(op, ct),
            _                                        => false,
        };

        if (success)
            await _opLog.MarkUndoneAsync(op.Id, ct);

        return success;
    }

    private async Task<bool> UndoDeleteAsync(OperationLogEntry op, CancellationToken ct)
    {
        if (op.EntityId is null) return false;
        await _mediaRepo.RestoreAsync(op.EntityId.Value, ct);
        _log.LogInformation("Undo delete: restored MediaItem {id}", op.EntityId);
        return true;
    }

    private async Task<bool> UndoTagAsync(OperationLogEntry op, CancellationToken ct)
    {
        if (op.EntityId is null || !long.TryParse(op.Payload, out var tagId)) return false;
        await _tagRepo.UntagMediaAsync(op.EntityId.Value, tagId, ct);
        _log.LogInformation("Undo tag: untagged MediaItem {id} tag {tagId}", op.EntityId, tagId);
        return true;
    }
}
