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

    // PERSISTENT CONNECTIONS: iOS PTP sours a client after repeated session
    // open/close cycles (our old per-operation Connect/Disconnect — including
    // the 20s watcher poll — eventually earns permanent 0x8007001E read
    // faults, while Explorer's single long-lived session keeps working).
    // We now mirror Explorer: connect once per device, reuse the session,
    // release only when the device disappears or the watchdog abandons it.
    private static readonly Dictionary<string, MediaDevice> _openDevices = new();
    private static readonly Dictionary<string, PhoneDevice> _knownPhones = new();

    private const int AcquireTimeoutMs = 30_000;   // waiting for another op to finish
    private const int QuickOpTimeoutMs = 30_000;   // device list / storage / thumbnail
    // Long operations (scan, download) are watchdogged by PROGRESS, not wall
    // time: they may run for hours as long as items/bytes keep advancing.
    // Only a stall (no progress at all for this window) aborts them.
    private const int StallTimeoutMs   = 120_000;

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
            var presentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var device in MediaDevice.GetDevices())
            {
                ct.ThrowIfCancellationRequested();
                presentIds.Add(device.DeviceId);

                // Known phone with a live session: reuse — NO reconnect cycle.
                if (_knownPhones.TryGetValue(device.DeviceId, out var known))
                {
                    result.Add(known);
                    continue;
                }

                try
                {
                    device.Connect();
                    // Only phones/cameras speak MTP/PTP. USB sticks and card
                    // readers also appear in the WPD list (wpdbusenum/MSC) —
                    // they must not trigger the Phone menu.
                    var protocol = Safe(() => device.Protocol) ?? "";
                    bool isMedia = protocol.Contains("MTP", StringComparison.OrdinalIgnoreCase)
                                || protocol.Contains("PTP", StringComparison.OrdinalIgnoreCase);
                    if (!isMedia)
                    {
                        try { device.Disconnect(); } catch { }
                        continue;
                    }

                    var phone = new PhoneDevice
                    {
                        DeviceId     = device.DeviceId,
                        FriendlyName = FirstNonEmpty(device.FriendlyName, device.Description, "Portable Device"),
                        Manufacturer = Safe(() => device.Manufacturer),
                        Model        = Safe(() => device.Model),
                        SerialNumber = Safe(() => device.SerialNumber),
                    };
                    // Keep the session OPEN — this is the persistent connection.
                    _openDevices[device.DeviceId] = device;
                    _knownPhones[device.DeviceId] = phone;
                    Services.RunLogger.Info("MtpDevice", $"Persistent session opened: {phone.FriendlyName}");
                    result.Add(phone);
                }
                catch
                {
                    // Device present but not accessible (locked / not trusted / busy) — skip.
                    try { device.Disconnect(); } catch { }
                }
            }

            // Devices that vanished: drop their cached sessions.
            foreach (var gone in _openDevices.Keys.Where(k => !presentIds.Contains(k)).ToList())
            {
                Services.RunLogger.Info("MtpDevice", "Device removed — releasing persistent session");
                try { _openDevices[gone].Disconnect(); } catch { }
                _openDevices.Remove(gone);
                _knownPhones.Remove(gone);
            }
            return result;
        }, QuickOpTimeoutMs, "device detection", ct);

    public async Task<IReadOnlyList<PhoneMediaItem>> GetMediaItemsAsync(
        string deviceId, IProgress<PhoneScanProgress>? progress = null, CancellationToken ct = default)
    {
        var result = await GetMediaItemsOnceAsync(deviceId, progress, ct);
        if (result.Count > 0) return result;

        // Empty result usually means the persistent session was negotiated
        // while the phone was locked — such a session NEVER shows storage,
        // even after unlock. Drop it and renegotiate once.
        Services.RunLogger.Info("MtpDevice",
            "Empty scan — dropping the cached session and renegotiating (session may predate unlock)");
        await InvalidateSessionAsync(deviceId, ct);
        await Task.Delay(2000, ct);
        return await GetMediaItemsOnceAsync(deviceId, progress, ct);
    }

    private Task InvalidateSessionAsync(string deviceId, CancellationToken ct)
        => RunGated<object?>(() =>
        {
            if (_openDevices.TryGetValue(deviceId, out var d))
            {
                try { d.Disconnect(); } catch { }
                _openDevices.Remove(deviceId);
                _knownPhones.Remove(deviceId);
            }
            return null;
        }, QuickOpTimeoutMs, "session invalidate", ct);

    private Task<IReadOnlyList<PhoneMediaItem>> GetMediaItemsOnceAsync(
        string deviceId, IProgress<PhoneScanProgress>? progress, CancellationToken ct)
    {
        var heartbeat = new long[] { Environment.TickCount64 };
        return WithDevice<IReadOnlyList<PhoneMediaItem>>(deviceId, device =>
        {
            // iOS needs time after connect to prepare its PTP photo database —
            // premature reads fail with 0x8007001E ("cannot read from the
            // specified device"). Retry with backoff instead of giving up.
            for (int attempt = 1; ; attempt++)
            {
                try { return ScanOnce(device); }
                catch (Exception ex) when (attempt <= 4 && IsDeviceReadFault(ex))
                {
                    var waitS = 8 * attempt;
                    Services.RunLogger.Warn("MtpDevice",
                        $"Read fault 0x8007001E — phone still preparing its photo database. Retry {attempt}/4 in {waitS}s");
                    var end = Environment.TickCount64 + waitS * 1000;
                    while (Environment.TickCount64 < end)
                    {
                        ct.ThrowIfCancellationRequested();
                        heartbeat[0] = Environment.TickCount64; // waiting deliberately — not stalled
                        Thread.Sleep(1000);
                    }
                }
            }

            IReadOnlyList<PhoneMediaItem> ScanOnce(MediaDevice device)
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
            // Manual breadth-first walk instead of a single recursive query:
            // some iPhone sessions instantly fault (0x8007001E) on the recursive
            // form while serving per-folder listings fine. Walking folder by
            // folder isolates faults to individual folders and yields partial
            // results instead of all-or-nothing.
            int faultedDirs = 0, okDirs = 0;
            Exception? lastFault = null;
            foreach (var root in FindMediaRoots(device))
            {
                var pending = new Queue<string>();
                pending.Enqueue(root);
                while (pending.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    heartbeat[0] = Environment.TickCount64;
                    var dir = pending.Dequeue();

                    try
                    {
                        foreach (var sub in device.EnumerateDirectories(dir))
                            pending.Enqueue(sub);
                    }
                    catch (Exception ex)
                    {
                        faultedDirs++; lastFault = ex;
                        Services.RunLogger.Warn("MtpDevice", $"Subfolder listing failed for '{dir}' — skipping its children");
                    }

                    try
                    {
                        foreach (var info in device.GetDirectoryInfo(dir).EnumerateFiles())
                        {
                            ct.ThrowIfCancellationRequested();
                            heartbeat[0] = Environment.TickCount64;
                            var ext = Path.GetExtension(info.Name).ToLowerInvariant();
                            if (!MediaExtensions.Contains(ext)) continue;
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
                        okDirs++;
                    }
                    catch (Exception ex)
                    {
                        faultedDirs++; lastFault = ex;
                        Services.RunLogger.Warn("MtpDevice", $"File listing failed for '{dir}' — folder skipped");
                    }
                }
            }
            Services.RunLogger.Info("MtpDevice", $"Folder walk: {okDirs} folders read, {faultedDirs} faulted");

            // Total blackout (nothing readable) → let the preparing-DB retry engage.
            if (items.Count == 0 && faultedDirs > 0 && lastFault is not null)
                throw lastFault;
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
            }
        }, StallTimeoutMs, "photo scan", ct, () => heartbeat[0]);
    }

    private static bool IsDeviceReadFault(Exception ex)
    {
        for (var e = (Exception?)ex; e is not null; e = e switch
             { AggregateException a => a.InnerExceptions.FirstOrDefault(), _ => e.InnerException })
        {
            if ((uint)e.HResult == 0x8007001E ||
                e.Message.Contains("cannot read from the specified device", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

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
    {
        var heartbeat = new long[] { Environment.TickCount64 };
        return WithDevice<object?>(deviceId, device =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            using var fs = new HeartbeatStream(File.Create(destinationPath), heartbeat);
            device.DownloadFile(itemId, fs);
            return null;
        }, StallTimeoutMs, "photo download", ct, () => heartbeat[0]);
    }

    /// <summary>Passthrough stream that records a progress tick on every write —
    /// lets the watchdog distinguish a slow large download from a stalled one.</summary>
    private sealed class HeartbeatStream(Stream inner, long[] heartbeat) : Stream
    {
        public override void Write(byte[] buffer, int offset, int count)
        { heartbeat[0] = Environment.TickCount64; inner.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer)
        { heartbeat[0] = Environment.TickCount64; inner.Write(buffer); }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

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
        string deviceId, Func<MediaDevice, T> action, int opTimeoutMs, string opName, CancellationToken ct,
        Func<long>? lastProgressTick = null)
        => RunGated(() =>
        {
            // Reuse the persistent session; open one only if we don't have it.
            if (!_openDevices.TryGetValue(deviceId, out var device))
            {
                device = MediaDevice.GetDevices().FirstOrDefault(d => d.DeviceId == deviceId)
                    ?? throw new InvalidOperationException("Device is no longer connected.");
                device.Connect();
                _openDevices[deviceId] = device;
            }
            try { return action(device); }
            catch
            {
                // A faulted session is not trustworthy — drop it so the next
                // operation negotiates a fresh one.
                try { device.Disconnect(); } catch { }
                _openDevices.Remove(deviceId);
                _knownPhones.Remove(deviceId);
                throw;
            }
        }, opTimeoutMs, opName, ct, lastProgressTick);

    /// <summary>
    /// Serialize a WPD operation through the gate with a watchdog. If the
    /// operation hangs (cable pulled mid-COM-call), the stuck gate is abandoned
    /// and replaced so the rest of the app self-heals; the caller gets a
    /// TimeoutException with recovery guidance.
    ///
    /// With <paramref name="lastProgressTick"/> supplied, the watchdog is
    /// PROGRESS-AWARE: <paramref name="opTimeoutMs"/> becomes a stall window —
    /// the op may run indefinitely while progress ticks keep advancing, and is
    /// only abandoned after a full window with no progress.
    /// </summary>
    private static Task<T> RunGated<T>(Func<T> action, int opTimeoutMs, string opName, CancellationToken ct,
        Func<long>? lastProgressTick = null)
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

                bool finished;
                if (lastProgressTick is null)
                {
                    finished = work.Wait(opTimeoutMs);
                }
                else
                {
                    // Poll in slices; only a stall (no progress for the whole
                    // window) counts as a hang. Cancellation abandons too —
                    // the COM call can't be interrupted any other way.
                    while (!(finished = work.Wait(2000)))
                    {
                        if (ct.IsCancellationRequested ||
                            Environment.TickCount64 - lastProgressTick() > opTimeoutMs)
                            break;
                    }
                }

                if (!finished)
                {
                    releaseNormally = false; // stuck holder keeps the old gate forever
                    lock (_gateSwap)
                    {
                        if (ReferenceEquals(_gate, gate))
                            _gate = new SemaphoreSlim(1, 1);
                    }
                    ct.ThrowIfCancellationRequested();
                    Services.RunLogger.Warn("MtpDevice",
                        $"'{opName}' made no progress for {opTimeoutMs / 1000}s — abandoned the stuck device connection and reset. " +
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
