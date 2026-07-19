using PhotoAlbum.Core.Domain;

namespace PhotoAlbum.Core.Interfaces;

/// <summary>
/// Persists a device's scanned inventory so any scan (UI or API) makes the
/// result available to the other, and reconnects are instant. Implemented by
/// PhoneInventoryService in the App layer.
/// </summary>
public interface IPhoneInventoryStore
{
    Task SaveInventoryAsync(string deviceKey, IReadOnlyList<PhoneMediaItem> items, CancellationToken ct = default);
    Task<IReadOnlyList<PhoneMediaItem>?> LoadInventoryAsync(string deviceKey, CancellationToken ct = default);

    /// <summary>UTC time the inventory was last written, or null if none.</summary>
    DateTime? GetInventoryTimeUtc(string deviceKey);
}
