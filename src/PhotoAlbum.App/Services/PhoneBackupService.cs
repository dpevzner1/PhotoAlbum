using PhotoAlbum.Core.Domain;
using PhotoAlbum.Core.Interfaces;
using System.IO;
using System.Text.Json;

namespace PhotoAlbum.App.Services;

/// <summary>
/// Backs up selected phone media to a local folder with BLAKE3 verification.
/// Flow per item: download to "<name>.part" → hash → rename to final name →
/// record hash in the per-device backup index (used for "already backed up"
/// detection on later syncs). Device-removed mid-transfer leaves no partials.
/// </summary>
public sealed class PhoneBackupService : IPhoneBackupService
{
    private readonly IDeviceService _devices;
    private readonly IRustHasher _hasher;
    private readonly IUserSettingsRepository _settings;
    private readonly IOperationLogRepository _opLog;

    private const string IndexKeyPrefix   = "phone.backup_index.";  // + device stable key → JSON hash set
    private const string DestKeyPrefix    = "phone.backup_dest.";   // + device stable key → last destination

    public PhoneBackupService(
        IDeviceService devices, IRustHasher hasher,
        IUserSettingsRepository settings, IOperationLogRepository opLog)
    {
        _devices  = devices;
        _hasher   = hasher;
        _settings = settings;
        _opLog    = opLog;
    }

    /// <summary>Default backup destination for a device (user-writable; never Program Files).</summary>
    public static string DefaultDestination(PhoneDevice device) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "PhotoAlbum", "Backups", Sanitize(device.FriendlyName));

    public async Task<string> GetSavedDestinationAsync(PhoneDevice device, CancellationToken ct = default)
        => await _settings.GetAsync(DestKeyPrefix + device.StableKey, ct) ?? DefaultDestination(device);

    public Task SaveDestinationAsync(PhoneDevice device, string destination, CancellationToken ct = default)
        => _settings.SetAsync(DestKeyPrefix + device.StableKey, destination, ct);

    /// <summary>Validates a chosen destination: must be creatable and writable.</summary>
    public static (bool Ok, string? Error) ValidateDestination(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, $".pa_write_probe_{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return (true, null);
        }
        catch (UnauthorizedAccessException)
        {
            return (false, "That folder is not writable. Pick a folder you have write access to (e.g. under your user profile).");
        }
        catch (Exception ex)
        {
            return (false, $"Cannot use that folder: {ex.Message}");
        }
    }

    /// <summary>Hashes previously backed up for this device (for skip/already-backed-up marks).</summary>
    public async Task<HashSet<string>> GetBackupIndexAsync(PhoneDevice device, CancellationToken ct = default)
    {
        var json = await _settings.GetAsync(IndexKeyPrefix + device.StableKey, ct);
        return json is null ? [] : JsonSerializer.Deserialize<HashSet<string>>(json) ?? [];
    }

    public async Task<PhoneBackupResult> BackupAsync(
        PhoneDevice device,
        IReadOnlyList<PhoneMediaItem> items,
        string destination,
        IProgress<PhoneBackupProgress>? progress = null,
        CancellationToken ct = default)
    {
        var (ok, error) = ValidateDestination(destination);
        if (!ok) throw new InvalidOperationException(error);

        var index  = await GetBackupIndexAsync(device, ct);
        var errors = new List<string>();
        int copied = 0, skipped = 0, failed = 0;
        long bytes = 0;

        RunLogger.Info("PhoneBackup", $"Backup started — {items.Count} item(s) → {destination}");

        for (int i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = items[i];
            progress?.Report(new PhoneBackupProgress(i, items.Count, item.Name, bytes));

            var finalPath = UniquePath(Path.Combine(destination, item.Name));
            var partPath  = finalPath + ".part";
            try
            {
                await _devices.DownloadItemAsync(device.DeviceId, item.ItemId, partPath, ct);
                var hash = await _hasher.HashFileAsync(partPath, ct);

                if (index.Contains(hash))
                {
                    File.Delete(partPath);
                    skipped++;
                    continue; // identical content already backed up previously
                }

                File.Move(partPath, finalPath);
                index.Add(hash);
                item.Blake3Hash = hash;
                bytes += item.SizeBytes;
                copied++;
                await _opLog.LogAsync("PhoneBackup", "File", null,
                    JsonSerializer.Serialize(new { device = device.FriendlyName, item.Name, hash, dest = finalPath }), ct);
            }
            catch (OperationCanceledException)
            {
                TryDelete(partPath);
                throw;
            }
            catch (Exception ex)
            {
                TryDelete(partPath);
                failed++;
                errors.Add($"{item.Name}: {ex.Message}");
                RunLogger.Warn("PhoneBackup", $"Item failed: {item.Name}", ex);
                // Device gone entirely? Stop early instead of failing every remaining item.
                if (ex is InvalidOperationException && ex.Message.Contains("no longer connected"))
                {
                    errors.Add("Device disconnected — backup stopped.");
                    break;
                }
            }
        }

        await _settings.SetAsync(IndexKeyPrefix + device.StableKey, JsonSerializer.Serialize(index), ct);
        await SaveDestinationAsync(device, destination, ct);
        progress?.Report(new PhoneBackupProgress(items.Count, items.Count, "", bytes));

        RunLogger.Info("PhoneBackup",
            $"Backup finished — copied {copied}, skipped {skipped} (duplicates), failed {failed}, {bytes / 1_048_576.0:F1} MB");
        return new PhoneBackupResult(copied, skipped, failed, bytes, destination, errors);
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir  = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext  = Path.GetExtension(path);
        for (int n = 1; ; n++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim();
    }
}
