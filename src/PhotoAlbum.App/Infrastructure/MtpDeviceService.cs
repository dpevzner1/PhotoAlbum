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
    private static readonly string[] VideoExtensions = [".mov", ".mp4", ".m4v", ".avi", ".3gp"];

    public Task<IReadOnlyList<PhoneDevice>> GetConnectedDevicesAsync(CancellationToken ct = default)
        => Task.Run<IReadOnlyList<PhoneDevice>>(() =>
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
        }, ct);

    public Task<IReadOnlyList<PhoneMediaItem>> GetMediaItemsAsync(
        string deviceId, IProgress<int>? progress = null, CancellationToken ct = default)
        => WithDevice<IReadOnlyList<PhoneMediaItem>>(deviceId, device =>
        {
            var items = new List<PhoneMediaItem>();

            // iPhones expose "<storage>\DCIM\1xxAPPLE\*". Find every DCIM root
            // across storages rather than assuming a fixed storage name.
            foreach (var dcim in FindDcimRoots(device))
            {
                foreach (var file in device.EnumerateFiles(dcim, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    MediaFileInfo info;
                    try { info = device.GetFileInfo(file); }
                    catch { continue; } // file vanished or unreadable — skip

                    var ext = Path.GetExtension(info.Name).ToLowerInvariant();
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
                    if (items.Count % 50 == 0) progress?.Report(items.Count);
                }
            }
            progress?.Report(items.Count);
            return items;
        }, ct);

    public Task DownloadItemAsync(string deviceId, string itemId, string destinationPath, CancellationToken ct = default)
        => WithDevice<object?>(deviceId, device =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            using var fs = File.Create(destinationPath);
            device.DownloadFile(itemId, fs);
            return null;
        }, ct);

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
        }, ct);

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static Task<T> WithDevice<T>(string deviceId, Func<MediaDevice, T> action, CancellationToken ct)
        => Task.Run(() =>
        {
            var device = MediaDevice.GetDevices().FirstOrDefault(d => d.DeviceId == deviceId)
                ?? throw new InvalidOperationException("Device is no longer connected.");
            device.Connect();
            try { return action(device); }
            finally { device.Disconnect(); }
        }, ct);

    private static IEnumerable<string> FindDcimRoots(MediaDevice device)
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
                    roots.AddRange(device.EnumerateDirectories(storage)
                        .Where(d => Path.GetFileName(d).Equals("DCIM", StringComparison.OrdinalIgnoreCase)));
                }
                catch (Exception ex)
                {
                    Services.RunLogger.Warn("MtpDevice", $"Storage '{storage}' not browsable", ex);
                }
            }
            Services.RunLogger.Info("MtpDevice",
                $"DCIM roots found: {(roots.Count == 0 ? "(none)" : string.Join(", ", roots))}");
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
