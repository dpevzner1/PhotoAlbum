using PhotoAlbum.Core.Domain;

namespace PhotoAlbum.Core.Interfaces;

public sealed record PhoneBackupProgress(
    int Done, int Total, string CurrentFile, long BytesCopied,
    int Copied = 0, int Skipped = 0, int Failed = 0)
{
    public int Remaining => Math.Max(0, Total - Done);
}

public sealed record PhoneBackupResult(
    int Copied, int Skipped, int Failed, long TotalBytes, string Destination, IReadOnlyList<string> Errors);

/// <summary>
/// Verified backup of phone media to a local folder.
/// Implemented by PhoneBackupService in the App layer; consumed by the UI and the local REST API.
/// </summary>
public interface IPhoneBackupService
{
    /// <summary>Last used (or default) destination folder for this device.</summary>
    Task<string> GetSavedDestinationAsync(PhoneDevice device, CancellationToken ct = default);

    Task SaveDestinationAsync(PhoneDevice device, string destination, CancellationToken ct = default);

    /// <summary>BLAKE3 hashes already backed up for this device.</summary>
    Task<HashSet<string>> GetBackupIndexAsync(PhoneDevice device, CancellationToken ct = default);

    Task<PhoneBackupResult> BackupAsync(
        PhoneDevice device,
        IReadOnlyList<PhoneMediaItem> items,
        string destination,
        IProgress<PhoneBackupProgress>? progress = null,
        CancellationToken ct = default);
}
