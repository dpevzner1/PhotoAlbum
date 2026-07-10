using PhotoAlbum.Core.Domain;
using PhotoAlbum.Core.Interfaces;
using System.IO;
using System.Text.Json;

namespace PhotoAlbum.App.Services;

/// <summary>
/// Resilient phone backup — journaled, idempotent, at-least-once with
/// content-addressed dedup (design: docs/phone-backup-resilience.md).
///
/// Per item: download → .part → size check vs device-reported bytes → BLAKE3 →
/// atomic rename → journal + hash-index write. Transient errors retry with
/// backoff; device loss pauses and waits for reconnect (re-resolving the
/// device by serial — WPD ids change on re-enumeration). A crash or power
/// loss leaves only .part files (deleted on resume) and a journal that knows
/// exactly what was copied.
/// </summary>
public sealed class PhoneBackupService : IPhoneBackupService
{
    private readonly IDeviceService _devices;
    private readonly IRustHasher _hasher;
    private readonly IUserSettingsRepository _settings;
    private readonly IOperationLogRepository _opLog;

    private const string IndexKeyPrefix = "phone.backup_index.";  // + device stable key → JSON hash set
    private const string DestKeyPrefix  = "phone.backup_dest.";   // + device stable key → last destination
    private const string JournalName    = ".photoalbum-backup-journal.json";

    private static readonly int[] RetryDelaysMs = [2000, 5000, 10000];
    private const int ReconnectWaitSeconds = 60;

    public PhoneBackupService(
        IDeviceService devices, IRustHasher hasher,
        IUserSettingsRepository settings, IOperationLogRepository opLog)
    {
        _devices  = devices;
        _hasher   = hasher;
        _settings = settings;
        _opLog    = opLog;
    }

    // ── Destination handling ──────────────────────────────────────────────────

    public static string DefaultDestination(PhoneDevice device) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "PhotoAlbum", "Backups", Sanitize(device.FriendlyName));

    public async Task<string> GetSavedDestinationAsync(PhoneDevice device, CancellationToken ct = default)
        => await _settings.GetAsync(DestKeyPrefix + device.StableKey, ct) ?? DefaultDestination(device);

    public Task SaveDestinationAsync(PhoneDevice device, string destination, CancellationToken ct = default)
        => _settings.SetAsync(DestKeyPrefix + device.StableKey, destination, ct);

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

    public async Task<HashSet<string>> GetBackupIndexAsync(PhoneDevice device, CancellationToken ct = default)
    {
        var json = await _settings.GetAsync(IndexKeyPrefix + device.StableKey, ct);
        return json is null ? [] : JsonSerializer.Deserialize<HashSet<string>>(json) ?? [];
    }

    // ── Backup run ────────────────────────────────────────────────────────────

    public async Task<PhoneBackupResult> BackupAsync(
        PhoneDevice device,
        IReadOnlyList<PhoneMediaItem> items,
        string destination,
        IProgress<PhoneBackupProgress>? progress = null,
        CancellationToken ct = default)
    {
        var (ok, error) = ValidateDestination(destination);
        if (!ok) throw new InvalidOperationException(error);

        CleanOrphanParts(destination);

        var journal = BackupJournal.LoadOrCreate(destination, device.StableKey, items.Count);
        var index   = await GetBackupIndexAsync(device, ct);
        var errors  = new List<string>();
        int copied = 0, skipped = 0, failed = 0;
        long bytes = 0;
        var deviceId = device.DeviceId; // may be re-resolved after a reconnect

        RunLogger.Info("PhoneBackup",
            $"Backup started — {items.Count} item(s) → {destination}" +
            (journal.CopiedCount > 0 ? $" (journal shows {journal.CopiedCount} already copied — will skip)" : ""));

        for (int i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = items[i];
            Report(i);

            // Resume path 1: this exact device file was already copied to this destination.
            if (journal.IsCopied(item.ItemId)) { skipped++; continue; }

            var finalPath = UniquePath(Path.Combine(destination, item.Name));
            var partPath  = finalPath + ".part";
            var itemDone  = false;

            for (int attempt = 0; attempt <= RetryDelaysMs.Length && !itemDone; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await _devices.DownloadItemAsync(deviceId, item.ItemId, partPath, ct);

                    // Corruption gate 1: the byte count must match what the device reported.
                    var actual = new FileInfo(partPath).Length;
                    if (item.SizeBytes > 0 && actual != item.SizeBytes)
                        throw new IOException($"Size mismatch: device reports {item.SizeBytes} bytes, received {actual}.");

                    var hash = await _hasher.HashFileAsync(partPath, ct);

                    // Resume path 2: identical content already exists in any previous backup.
                    if (index.Contains(hash))
                    {
                        File.Delete(partPath);
                        journal.MarkCopied(item.ItemId, hash, "(duplicate — skipped)");
                        skipped++;
                        itemDone = true;
                        break;
                    }

                    File.Move(partPath, finalPath); // atomic commit
                    // Preserve capture time on the backed-up file so Explorer
                    // and library import show the real date, not the copy date.
                    if (item.DateTaken is { } taken)
                        { try { File.SetLastWriteTime(finalPath, taken); } catch { } }
                    index.Add(hash);
                    item.Blake3Hash = hash;
                    bytes += item.SizeBytes;
                    copied++;
                    itemDone = true;

                    // Durable state after EVERY file: journal + hash index.
                    journal.MarkCopied(item.ItemId, hash, Path.GetFileName(finalPath));
                    await _settings.SetAsync(IndexKeyPrefix + device.StableKey,
                        JsonSerializer.Serialize(index), ct);
                    await _opLog.LogAsync("PhoneBackup", "File", null,
                        JsonSerializer.Serialize(new { device = device.FriendlyName, item.Name, hash, dest = finalPath }), ct);
                }
                catch (OperationCanceledException)
                {
                    TryDelete(partPath);
                    journal.Save();
                    throw;
                }
                catch (Exception ex)
                {
                    TryDelete(partPath);

                    // Device gone? Pause and wait for it to come back (unlock/jitter).
                    if (await IsDeviceGoneAsync(device))
                    {
                        RunLogger.Warn("PhoneBackup",
                            $"Device lost at item {i + 1}/{items.Count} — waiting up to {ReconnectWaitSeconds}s for reconnect");
                        Report(i, "Connection lost — waiting for the phone to come back…");
                        var reconnected = await WaitForReconnectAsync(device, ct);
                        if (reconnected is null)
                        {
                            failed++;
                            errors.Add($"{item.Name}: device disconnected and did not return — backup paused. " +
                                       "Reconnect and run backup again to resume.");
                            journal.Save();
                            goto RunEnded;
                        }
                        deviceId = reconnected.DeviceId;
                        RunLogger.Info("PhoneBackup", "Device reconnected — resuming");
                        attempt--; // reconnects don't consume a retry attempt
                        continue;
                    }

                    if (attempt < RetryDelaysMs.Length)
                    {
                        RunLogger.Warn("PhoneBackup",
                            $"Item '{item.Name}' attempt {attempt + 1} failed — retrying in {RetryDelaysMs[attempt] / 1000}s", ex);
                        await Task.Delay(RetryDelaysMs[attempt], ct);
                    }
                    else
                    {
                        failed++;
                        errors.Add($"{item.Name}: {ex.Message}");
                        journal.MarkFailed(item.ItemId, ex.Message);
                        RunLogger.Warn("PhoneBackup", $"Item failed after {RetryDelaysMs.Length + 1} attempts: {item.Name}", ex);
                    }
                }
            }
        }

    RunEnded:
        journal.Save();
        await _settings.SetAsync(IndexKeyPrefix + device.StableKey, JsonSerializer.Serialize(index), ct);
        await SaveDestinationAsync(device, destination, ct);
        Report(items.Count, "");

        RunLogger.Info("PhoneBackup",
            $"Backup finished — copied {copied}, skipped {skipped} (already backed up), failed {failed}, " +
            $"{bytes / 1_048_576.0:F1} MB. Journal: {journal.CopiedCount}/{items.Count} accounted for.");
        return new PhoneBackupResult(copied, skipped, failed, bytes, destination, errors);

        void Report(int done, string? file = null) =>
            progress?.Report(new PhoneBackupProgress(
                done, items.Count, file ?? (done < items.Count ? items[done].Name : ""),
                bytes, copied, skipped, failed));
    }

    // ── Reconnect handling ────────────────────────────────────────────────────

    private async Task<bool> IsDeviceGoneAsync(PhoneDevice device)
    {
        try
        {
            var current = await _devices.GetConnectedDevicesAsync();
            return !current.Any(d => d.StableKey == device.StableKey);
        }
        catch { return true; }
    }

    private async Task<PhoneDevice?> WaitForReconnectAsync(PhoneDevice device, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(ReconnectWaitSeconds);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(3000, ct);
            try
            {
                var match = (await _devices.GetConnectedDevicesAsync(ct))
                    .FirstOrDefault(d => d.StableKey == device.StableKey);
                if (match is not null) return match;
            }
            catch { /* keep waiting */ }
        }
        return null;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void CleanOrphanParts(string destination)
    {
        try
        {
            foreach (var part in Directory.EnumerateFiles(destination, "*.part"))
            {
                File.Delete(part);
                RunLogger.Info("PhoneBackup", $"Removed orphaned partial file: {Path.GetFileName(part)}");
            }
        }
        catch { /* best effort */ }
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

    // ── Journal ───────────────────────────────────────────────────────────────

    /// <summary>Per-destination durable record of what has been copied (crash-safe resume).</summary>
    private sealed class BackupJournal
    {
        public int Version { get; set; } = 1;
        public string DeviceKey { get; set; } = "";
        public DateTime StartedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public int Total { get; set; }
        public Dictionary<string, JournalItem> Items { get; set; } = [];

        public sealed class JournalItem
        {
            public string S { get; set; } = "";   // copied | failed
            public string? H { get; set; }        // blake3
            public string? N { get; set; }        // final file name
        }

        private string _path = "";

        public int CopiedCount => Items.Count(kv => kv.Value.S == "copied");
        public bool IsCopied(string itemId) => Items.TryGetValue(itemId, out var it) && it.S == "copied";

        public void MarkCopied(string itemId, string hash, string finalName)
        {
            Items[itemId] = new JournalItem { S = "copied", H = hash, N = finalName };
            Save();
        }

        public void MarkFailed(string itemId, string reason)
        {
            Items[itemId] = new JournalItem { S = "failed", N = Truncate(reason) };
            Save();
        }

        public void Save()
        {
            try
            {
                UpdatedUtc = DateTime.UtcNow;
                // Write-then-rename so the journal itself can't be torn.
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this));
                File.Move(tmp, _path, overwrite: true);
            }
            catch (Exception ex)
            {
                RunLogger.Warn("PhoneBackup", "Journal write failed (progress still in memory)", ex);
            }
        }

        public static BackupJournal LoadOrCreate(string destination, string deviceKey, int total)
        {
            var path = Path.Combine(destination, JournalName);
            BackupJournal journal;
            try
            {
                journal = File.Exists(path)
                    ? JsonSerializer.Deserialize<BackupJournal>(File.ReadAllText(path)) ?? new BackupJournal()
                    : new BackupJournal { StartedUtc = DateTime.UtcNow };
            }
            catch
            {
                journal = new BackupJournal { StartedUtc = DateTime.UtcNow }; // corrupt journal → start fresh
            }
            journal._path = path;
            journal.DeviceKey = deviceKey;
            journal.Total = total;
            return journal;
        }

        private static string Truncate(string s) => s.Length <= 200 ? s : s[..200];
    }
}
