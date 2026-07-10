using MediaDevices;
using PhotoAlbum.Core.Domain;
using PhotoAlbum.Core.Interfaces;
using System.IO;

namespace PhotoAlbum.App.Infrastructure;

/// <summary>
/// IDeviceService adapter over the MediaDevices (WPD/MTP) library.
/// Read-only: never writes to the phone. All WPD COM calls run on the
/// thread pool because they are synchronous and can block for seconds.
/// </summary>
public sealed class MtpDeviceService : IDeviceService
{
    // WPD COM connections are not safe against concurrent Connect/Disconnect:
    // the DeviceWatcher's periodic poll disconnecting a device mid-enumeration
    // kills the enumerator with 0x802A0002 ("Shutdown was already called").
    // All device operations are therefore serialized through this gate.
    //
    // The gate is REPLACEABLE: a WPD call can block forever when the cable is
    // pulled mid-operation. When an operation exceeds its watchdog timeout we
    // abandon the stuck gate (its holder never returns) and install a fresh
    // one, so the watcher and future operations self-heal instead of the whole
    // phone feature freezing until app restart.
    private static SemaphoreSlim _gate = new(1, 1);
    private static readonly object _gateSwap = new();

    private const int AcquireTimeoutMs = 30_000;   // waiting for another op to finish
    private const int QuickOpTimeoutMs = 30_000;   // device list / storage / thumbnail
    private const int LongOpTimeoutMs  = 15 * 60_000; // full scan / large video download

    private static readonly string[] VideoExtensions = [".mov", ".mp4", ".m4v", ".avi", ".3gp", ".mkv", ".wmv"];

    // Only surface real media — storage roots can contain sidecar/other files.
    // Kept in sync with the Rust scanner's SUPPORTED_EXTENSIONS.
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".heic", ".heif", ".png", ".dng", ".gif", ".webp", ".bmp", ".tif", ".tiff",
        ".mov", ".mp4", ".m4v", ".avi", ".3gp", ".mkv", ".wmv",
    };

    public Task<IReadOnlyList<PhoneDevice>> GetConnectedDevicesAsync(CancellationToken ct = default)
        => RunGated<IReadOnlyList<PhoneDevice>>(() =>
        {
            var result = new List<PhoneDevice>();
            foreach (var device in MediaDevice.GetDevices())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    device.Connect();
                    try
                    {
                        // Only phones/cameras speak MTP/PTP. USB sticks and card
                        // readers also appear in the WPD list (wpdbusenum/MSC) —
                        // they must not trigger the Phone menu.
                        var protocol = Safe(() => device.Protocol) ?? "";
                        bool isMedia = protocol.Contains("MTP", StringComparison.OrdinalIgnoreCase)
                                    || protocol.Contains("PTP", StringComparison.OrdinalIgnoreCase);
                        if (!isMedia) continue;

                        result.Add(new PhoneDevice
                        {
                            DeviceId     = device.DeviceId,
                            FriendlyName = FirstNonEmpty(device.FriendlyName, device.Description, "Portable Device"),
                            Manufacturer = Safe(() => device.Manufacturer),
                            Model        = Safe(() => device.Model),
                            SerialNumber = Safe(() => device.SerialNumber),
                        });
                    }
                    finally { device.Disconnect(); }
                }
                catch
                {
                    // Device present but not accessible (locked / not trusted / busy) — skip.
                }
            }
            return result;
        }, QuickOpTimeoutMs, "device detection", ct);

    public Task<IReadOnlyList<PhoneMediaItem>> GetMediaItemsAsync(
        string deviceId, IProgress<PhoneScanProgress>? progress = null, CancellationToken ct = default)
        => WithDevice<IReadOnlyList<PhoneMediaItem>>(deviceId, device =>
        {
            var items = new List<PhoneMediaItem>();
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long bytes = 0;
            int dupSkipped = 0;

            // Older iOS exposes "<storage>\DCIM\1xxAPPLE\*"; iOS 17+ exposes
            // date-named folders (e.g. "202105_h") directly under the storage
            // root with no DCIM level. Use DCIM when present, otherwise scan
            // the storage root and filter to media extensions.
            //
            // Enumerate MediaFileInfo objects directly (bulk metadata) instead
            // of a per-file GetFileInfo round-trip — orders of magnitude faster
            // over MTP on large libraries.
            foreach (var root in FindMediaRoots(device))
            {
                IEnumerable<MediaFileInfo> files;
                try { files = device.GetDirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories); }
                catch (Exception ex)
                {
                    Services.RunLogger.Warn("MtpDevice", $"Cannot enumerate '{root}'", ex);
                    continue;
                }

                foreach (var info in files)
                {
                    ct.ThrowIfCancellationRequested();
                    var ext = Path.GetExtension(info.Name).ToLowerInvariant();
                    if (!MediaExtensions.Contains(ext)) continue;

                    // Defensive: never count the same device object twice, even if
                    // roots overlap or the device replays entries.
                    if (!seenIds.Add(info.FullName)) { dupSkipped++; continue; }

                    items.Add(new PhoneMediaItem
                    {
                        ItemId    = info.FullName,
                        Name      = info.Name,
                        Folder    = TrimToDcim(Path.GetDirectoryName(info.FullName) ?? ""),
                        SizeBytes = (long)info.Length,
                        DateTaken = info.DateAuthored != default ? info.DateAuthored
                                  : info.LastWriteTime != default ? info.LastWriteTime : null,
                        IsVideo   = VideoExtensions.Contains(ext),
                    });
                    bytes += (long)info.Length;
                    if (items.Count % 25 == 0) progress?.Report(new PhoneScanProgress(items.Count, bytes));
                }
            }
            progress?.Report(new PhoneScanProgress(items.Count, bytes));

            // Post-scan diagnostics: extension byte histogram + duplicate stats.
            // Makes "total exceeds device storage" analyzable from the run log
            // (iCloud-optimized originals / Live Photo .MOVs / HEIC+JPG pairs).
            var histogram = items
                .GroupBy(i => Path.GetExtension(i.Name).ToLowerInvariant())
                .OrderByDescending(g => g.Sum(i => i.SizeBytes))
                .Select(g => $"{g.Key}×{g.Count()}={g.Sum(i => i.SizeBytes) / 1_073_741_824.0:F1}GB");
            var pairNames = items.Where(i => !i.IsVideo).Select(i => Path.GetFileNameWithoutExtension(i.Name))
                .GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Count(g => g.Count() > 1);
            Services.RunLogger.Info("MtpDevice",
                $"Scan complete: {items.Count} items, {bytes / 1_073_741_824.0:F1} GB advertised. " +
                $"Duplicate ids skipped: {dupSkipped}. Same-name photo pairs (HEIC+JPG?): {pairNames}. " +
                $"By type: {string.Join("  ", histogram)}");
            return items;
        }, LongOpTimeoutMs, "photo scan", ct);

    public Task<PhoneStorageInfo?> GetStorageInfoAsync(string deviceId, CancellationToken ct = default)
        => WithDevice<PhoneStorageInfo?>(deviceId, device =>
        {
            try
            {
                ulong capacity = 0, free = 0;
                foreach (var storage in device.EnumerateDirectories(@"\"))
                {
                    try
                    {
                        var info = device.GetStorageInfo(storage);
                        if (info is null) continue;
                        capacity += info.Capacity;
                        free     += info.FreeSpaceInBytes;
                    }
                    catch { /* storage won't report — skip */ }
                }
                return capacity > 0 ? new PhoneStorageInfo(capacity, free) : null;
            }
            catch { return null; }
        }, QuickOpTimeoutMs, "storage info", ct);

    public Task DownloadItemAsync(string deviceId, string itemId, string destinationPath, CancellationToken ct = default)
        => WithDevice<object?>(deviceId, device =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            using var fs = File.Create(destinationPath);
            device.DownloadFile(itemId, fs);
            return null;
        }, LongOpTimeoutMs, "photo download", ct);

    public Task<byte[]?> GetThumbnailAsync(string deviceId, string itemId, CancellationToken ct = default)
        => WithDevice<byte[]?>(deviceId, device =>
        {
            try
            {
                using var ms = new MemoryStream();
                device.DownloadThumbnail(itemId, ms);
                return ms.Length > 0 ? ms.ToArray() : null;
            }
            catch { return null; } // many devices simply don't serve thumbnails
        }, QuickOpTimeoutMs, "thumbnail", ct);

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static Task<T> WithDevice<T>(
        string deviceId, Func<MediaDevice, T> action, int opTimeoutMs, string opName, CancellationToken ct)
        => RunGated(() =>
        {
            var device = MediaDevice.GetDevices().FirstOrDefault(d => d.DeviceId == deviceId)
                ?? throw new InvalidOperationException("Device is no longer connected.");
            device.Connect();
            try { return action(device); }
            finally { try { device.Disconnect(); } catch { } }
        }, opTimeoutMs, opName, ct);

    /// <summary>
    /// Serialize a WPD operation through the gate with a watchdog. If the
    /// operation hangs (cable pulled mid-COM-call), the stuck gate is abandoned
    /// and replaced so the rest of the app self-heals; the caller gets a
    /// TimeoutException with recovery guidance.
    /// </summary>
    private static Task<T> RunGated<T>(Func<T> action, int opTimeoutMs, string opName, CancellationToken ct)
        => Task.Run(() =>
        {
            var gate = _gate;
            if (!gate.Wait(AcquireTimeoutMs, ct))
                throw new TimeoutException(
                    $"The phone connection is busy with another operation ({opName} could not start).");

            var releaseNormally = true;
            try
            {
                // The COM call itself cannot be cancelled — run it on its own
                // task and watchdog it from here.
                var work = Task.Run(action, CancellationToken.None);
                if (!work.Wait(opTimeoutMs))
                {
                    releaseNormally = false; // stuck holder keeps the old gate forever
                    lock (_gateSwap)
                    {
                        if (ReferenceEquals(_gate, gate))
                            _gate = new SemaphoreSlim(1, 1);
                    }
                    Services.RunLogger.Warn("MtpDevice",
                        $"'{opName}' hung for {opTimeoutMs / 1000}s — abandoned the stuck device connection and reset. " +
                        "Unplug/replug the phone if it stays unresponsive.");
                    throw new TimeoutException(
                        $"The phone stopped responding during {opName}. Unplug and replug it, then try again.");
                }
                return work.GetAwaiter().GetResult();
            }
            finally
            {
                if (releaseNormally) { try { gate.Release(); } catch { } }
            }
        }, ct);

    private static IEnumerable<string> FindMediaRoots(MediaDevice device)
    {
        var roots = new List<string>();
        try
        {
            var storages = device.EnumerateDirectories(@"\").ToList();
            Services.RunLogger.Info("MtpDevice",
                $"Storages on '{device.FriendlyName}': {(storages.Count == 0 ? "(none — device locked or not trusted?)" : string.Join(", ", storages))}");
            foreach (var storage in storages)
            {
                try
                {
                    // Classic layout: <storage>\DCIM\... — scan just DCIM.
                    // iOS 17+ layout: date folders (202105_h, ...) directly under
                    // the storage root, no DCIM — scan the whole storage and let
                    // the media-extension filter do the narrowing.
                    var dcim = device.EnumerateDirectories(storage)
                        .Where(d => Path.GetFileName(d).Equals("DCIM", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (dcim.Count > 0)
                    {
                        roots.AddRange(dcim);
                        Services.RunLogger.Info("MtpDevice", $"'{storage}': classic DCIM layout");
                    }
                    else
                    {
                        roots.Add(storage);
                        Services.RunLogger.Info("MtpDevice",
                            $"'{storage}': no DCIM folder — scanning storage root (iOS 17+ date-folder layout)");
                    }
                }
                catch (Exception ex)
                {
                    Services.RunLogger.Warn("MtpDevice", $"Storage '{storage}' not browsable", ex);
                }
            }
        }
        catch (Exception ex)
        {
            Services.RunLogger.Warn("MtpDevice", "Device root not browsable (locked mid-call?)", ex);
        }
        return roots;
    }

    private static string TrimToDcim(string path)
    {
        var idx = path.IndexOf("DCIM", StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? path[idx..].Replace('\\', '/') : path.Replace('\\', '/');
    }

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    private static string? Safe(Func<string?> get)
    {
        try { return get(); } catch { return null; }
    }
}
