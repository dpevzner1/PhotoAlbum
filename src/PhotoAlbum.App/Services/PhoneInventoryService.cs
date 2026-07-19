using PhotoAlbum.Core.Domain;
using PhotoAlbum.Core.Interfaces;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PhotoAlbum.App.Services;

/// <summary>
/// Persistent per-device cache so a reconnect is instant and syncs are
/// incremental. Stores, under %LOCALAPPDATA%\PhotoAlbum\phone-cache\&lt;device&gt;\:
///   • inventory.json — the last scan's item list (id, name, folder, size,
///     date, isVideo) so the grid shows immediately before a rescan finishes;
///   • thumbs\&lt;hash&gt;.jpg — thumbnail bytes, so paging/revisits never re-fetch
///     over MTP.
/// All operations are best-effort and never throw into the UI.
/// </summary>
public sealed class PhoneInventoryService : IPhoneInventoryStore
{
    public DateTime? GetInventoryTimeUtc(string deviceKey)
    {
        try
        {
            var p = InventoryPath(deviceKey);
            return File.Exists(p) ? File.GetLastWriteTimeUtc(p) : null;
        }
        catch { return null; }
    }

    private readonly string _root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PhotoAlbum", "phone-cache");

    private string DeviceDir(string deviceKey) => Path.Combine(_root, Sanitize(deviceKey));
    private string ThumbsDir(string deviceKey) => Path.Combine(DeviceDir(deviceKey), "thumbs");
    private string InventoryPath(string deviceKey) => Path.Combine(DeviceDir(deviceKey), "inventory.json");
    private string ThumbPath(string deviceKey, string itemId) =>
        Path.Combine(ThumbsDir(deviceKey), StableHash(itemId) + ".jpg");

    // ── Inventory ───────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<PhoneMediaItem>?> LoadInventoryAsync(string deviceKey, CancellationToken ct = default)
    {
        try
        {
            var path = InventoryPath(deviceKey);
            if (!File.Exists(path)) return null;
            await using var fs = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<List<PhoneMediaItem>>(fs, cancellationToken: ct);
        }
        catch { return null; }
    }

    public async Task SaveInventoryAsync(string deviceKey, IReadOnlyList<PhoneMediaItem> items, CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(DeviceDir(deviceKey));
            var tmp = InventoryPath(deviceKey) + ".tmp";
            await using (var fs = File.Create(tmp))
                await JsonSerializer.SerializeAsync(fs, items, cancellationToken: ct);
            File.Move(tmp, InventoryPath(deviceKey), overwrite: true); // atomic
            RunLogger.Info("PhoneInventory", $"Cached inventory: {items.Count} items for device {deviceKey}");
        }
        catch (Exception ex) { RunLogger.Warn("PhoneInventory", "Inventory cache write failed", ex); }
    }

    /// <summary>
    /// Merge a fresh scan into the cached inventory: keep prior entries, add new
    /// item ids, drop ids no longer present. Returns the merged list and how many
    /// were newly added (for "N new since last sync").
    /// </summary>
    public (IReadOnlyList<PhoneMediaItem> Merged, int Added) Merge(
        IReadOnlyList<PhoneMediaItem>? cached, IReadOnlyList<PhoneMediaItem> fresh)
    {
        if (cached is null || cached.Count == 0) return (fresh, fresh.Count);
        var cachedIds = cached.Select(i => i.ItemId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = fresh.Count(i => !cachedIds.Contains(i.ItemId));
        // fresh is authoritative for the current device state.
        return (fresh, added);
    }

    // ── Thumbnails ──────────────────────────────────────────────────────────

    public async Task<byte[]?> TryLoadThumbnailAsync(string deviceKey, string itemId, CancellationToken ct = default)
    {
        try
        {
            var path = ThumbPath(deviceKey, itemId);
            return File.Exists(path) ? await File.ReadAllBytesAsync(path, ct) : null;
        }
        catch { return null; }
    }

    public async Task SaveThumbnailAsync(string deviceKey, string itemId, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(ThumbsDir(deviceKey));
            await File.WriteAllBytesAsync(ThumbPath(deviceKey, itemId), bytes);
        }
        catch { /* best effort */ }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string StableHash(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes)[..24].ToLowerInvariant();
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "device" : name.Trim();
    }
}
