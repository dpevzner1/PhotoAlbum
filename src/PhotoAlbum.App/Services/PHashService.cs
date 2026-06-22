using Microsoft.Extensions.Logging;
using PhotoAlbum.Core.Interfaces;

namespace PhotoAlbum.App.Services;

/// <summary>
/// Background service that computes perceptual hashes for any MediaItem
/// that doesn't have one yet. Runs in batches to avoid blocking the UI.
/// </summary>
public sealed class PHashService(
    IMediaItemRepository mediaRepo,
    IRustPHasher pHasher,
    ILogger<PHashService> logger)
{
    private const int BatchSize = 50;

    /// <summary>
    /// Process one batch of up to <see cref="BatchSize"/> items missing a pHash.
    /// Returns the number of items successfully hashed.
    /// </summary>
    public async Task<int> ProcessBatchAsync(CancellationToken ct = default)
    {
        var items = await mediaRepo.GetItemsMissingPHashAsync(BatchSize, ct);
        if (items.Count == 0)
            return 0;

        int count = 0;
        foreach (var (id, filePath) in items)
        {
            if (ct.IsCancellationRequested) break;
            if (filePath is null) continue;

            try
            {
                var hash = await pHasher.PHashFileAsync(filePath, ct);
                if (hash.HasValue)
                {
                    await mediaRepo.SetPHashAsync(id, hash.Value, ct);
                    count++;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "pHash failed for item {Id} at {Path}", id, filePath);
            }
        }

        return count;
    }

    /// <summary>
    /// Fill all missing pHashes by running batches until none remain.
    /// Progress callback receives (processed, found) counts.
    /// </summary>
    public async Task FillAllAsync(
        IProgress<(int Processed, int Total)>? progress = null,
        CancellationToken ct = default)
    {
        int processed = 0;
        while (!ct.IsCancellationRequested)
        {
            var n = await ProcessBatchAsync(ct);
            if (n == 0) break;
            processed += n;
            progress?.Report((processed, -1));
            await Task.Delay(50, ct); // yield between batches
        }
    }
}
