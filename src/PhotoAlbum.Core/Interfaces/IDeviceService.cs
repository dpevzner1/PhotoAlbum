using PhotoAlbum.Core.Domain;

namespace PhotoAlbum.Core.Interfaces;

/// <summary>
/// Read-only access to a phone connected over MTP/PTP (WPD).
/// Implemented by MtpDeviceService in the App layer; mockable for tests.
/// All device access is read-only — we never write to the phone.
/// </summary>
public interface IDeviceService
{
    /// <summary>Portable devices currently visible to Windows (unlocked + trusted only).</summary>
    Task<IReadOnlyList<PhoneDevice>> GetConnectedDevicesAsync(CancellationToken ct = default);

    /// <summary>
    /// Enumerate photos/videos under the device's DCIM tree.
    /// Albums are NOT available over MTP — folder structure only (see docs/iphone-backup-feasibility.md).
    /// </summary>
    Task<IReadOnlyList<PhoneMediaItem>> GetMediaItemsAsync(
        string deviceId, IProgress<int>? progress = null, CancellationToken ct = default);

    /// <summary>Download one item's bytes to <paramref name="destinationPath"/> (caller picks a temp/.part path).</summary>
    Task DownloadItemAsync(
        string deviceId, string itemId, string destinationPath, CancellationToken ct = default);

    /// <summary>Read a small thumbnail for grid display; null when the device provides none.</summary>
    Task<byte[]?> GetThumbnailAsync(string deviceId, string itemId, CancellationToken ct = default);
}
