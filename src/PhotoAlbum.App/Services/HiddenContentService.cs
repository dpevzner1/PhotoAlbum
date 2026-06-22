using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PhotoAlbum.Core.Interfaces;

namespace PhotoAlbum.App.Services;

/// <summary>
/// Manages the hidden-content vault: which albums and tags are hidden,
/// which individual media IDs are therefore excluded, and the PIN lock.
/// Persists config in UserSettings. ExcludedMediaIds is rebuilt in memory
/// on startup and whenever the hidden set changes.
/// </summary>
public sealed class HiddenContentService : PhotoAlbum.Core.Interfaces.IHiddenContentManager
{
    private readonly IUserSettingsRepository _settings;
    private readonly IAlbumRepository        _albumRepo;
    private readonly ITagRepository          _tagRepo;
    private readonly IMediaItemRepository    _mediaRepo;

    private HashSet<long> _hiddenAlbumIds = [];
    private HashSet<long> _hiddenTagIds   = [];

    public IReadOnlySet<long> HiddenAlbumIds => _hiddenAlbumIds;
    public IReadOnlySet<long> HiddenTagIds   => _hiddenTagIds;

    /// <summary>Union of all media belonging to hidden albums or bearing hidden tags.</summary>
    public HashSet<long> ExcludedMediaIds { get; private set; } = [];

    public bool IsUnlocked { get; private set; }
    public bool HasPin     { get; private set; }

    /// <summary>Fired when the lock state flips.</summary>
    public event Action? LockStateChanged;

    /// <summary>Fired when the hidden album/tag lists or ExcludedMediaIds change.</summary>
    public event Action? HiddenSetChanged;

    private const string KeyAlbums = "hidden.album_ids";
    private const string KeyTags   = "hidden.tag_ids";
    private const string KeyPin    = "hidden.pin_sha256";

    public HiddenContentService(
        IUserSettingsRepository settings,
        IAlbumRepository albumRepo,
        ITagRepository tagRepo,
        IMediaItemRepository mediaRepo)
    {
        _settings  = settings;
        _albumRepo = albumRepo;
        _tagRepo   = tagRepo;
        _mediaRepo = mediaRepo;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var albumJson = await _settings.GetAsync(KeyAlbums, ct);
        var tagJson   = await _settings.GetAsync(KeyTags,   ct);
        var pinHash   = await _settings.GetAsync(KeyPin,    ct);

        _hiddenAlbumIds = albumJson is null
            ? [] : (JsonSerializer.Deserialize<HashSet<long>>(albumJson) ?? []);
        _hiddenTagIds = tagJson is null
            ? [] : (JsonSerializer.Deserialize<HashSet<long>>(tagJson) ?? []);

        HasPin = !string.IsNullOrEmpty(pinHash);

        // If nothing is hidden yet, start in an unlocked state — no point showing a lock screen
        if (_hiddenAlbumIds.Count == 0 && _hiddenTagIds.Count == 0)
            IsUnlocked = true;

        await RebuildExcludedSetAsync(ct);
    }

    // ── Lock / unlock ─────────────────────────────────────────────────────────

    /// <summary>Unlock without a PIN (only valid when HasPin is false).</summary>
    public bool TryUnlockNoPin()
    {
        if (HasPin) return false;
        IsUnlocked = true;
        LockStateChanged?.Invoke();
        return true;
    }

    /// <summary>Unlock with a PIN. Returns false if the PIN is wrong.</summary>
    /// <summary>IHiddenContentManager bridge — accepts null/empty when no PIN is configured.</summary>
    public Task<bool> TryUnlockAsync(string? pin, CancellationToken ct = default) =>
        HasPin ? TryUnlockWithPinAsync(pin ?? "", ct)
               : Task.FromResult(TryUnlockNoPin());

    public async Task<bool> TryUnlockWithPinAsync(string pin, CancellationToken ct = default)
    {
        var storedHash = await _settings.GetAsync(KeyPin, ct);
        if (!string.Equals(Sha256Hex(pin), storedHash, StringComparison.OrdinalIgnoreCase))
            return false;
        IsUnlocked = true;
        LockStateChanged?.Invoke();
        return true;
    }

    public void Lock()
    {
        if (ExcludedMediaIds.Count == 0 && !HasPin) return; // nothing to lock
        IsUnlocked = false;
        LockStateChanged?.Invoke();
    }

    public async Task SetPinAsync(string pin, CancellationToken ct = default)
    {
        await _settings.SetAsync(KeyPin, Sha256Hex(pin), ct);
        HasPin = true;
    }

    public async Task ClearPinAsync(CancellationToken ct = default)
    {
        await _settings.DeleteAsync(KeyPin, ct);
        HasPin = false;
    }

    // ── Album / tag hiding ────────────────────────────────────────────────────

    public bool IsAlbumHidden(long albumId) => _hiddenAlbumIds.Contains(albumId);
    public bool IsTagHidden(long tagId)     => _hiddenTagIds.Contains(tagId);

    public async Task HideAlbumAsync(long albumId, CancellationToken ct = default)
    {
        if (!_hiddenAlbumIds.Add(albumId)) return;
        await SaveAlbumIdsAsync(ct);
        await RebuildExcludedSetAsync(ct);
        HiddenSetChanged?.Invoke();
    }

    public async Task UnhideAlbumAsync(long albumId, CancellationToken ct = default)
    {
        if (!_hiddenAlbumIds.Remove(albumId)) return;
        await SaveAlbumIdsAsync(ct);
        await RebuildExcludedSetAsync(ct);
        HiddenSetChanged?.Invoke();
    }

    public async Task HideTagAsync(long tagId, CancellationToken ct = default)
    {
        if (!_hiddenTagIds.Add(tagId)) return;
        await SaveTagIdsAsync(ct);
        await RebuildExcludedSetAsync(ct);
        HiddenSetChanged?.Invoke();
    }

    public async Task UnhideTagAsync(long tagId, CancellationToken ct = default)
    {
        if (!_hiddenTagIds.Remove(tagId)) return;
        await SaveTagIdsAsync(ct);
        await RebuildExcludedSetAsync(ct);
        HiddenSetChanged?.Invoke();
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    private async Task RebuildExcludedSetAsync(CancellationToken ct = default)
    {
        var set = new HashSet<long>();

        foreach (var albumId in _hiddenAlbumIds)
        {
            var ids = await _albumRepo.GetMediaIdsAsync(albumId, ct);
            set.UnionWith(ids);
        }

        if (_hiddenTagIds.Count > 0)
        {
            var items = await _mediaRepo.QueryAsync(
                new Core.Interfaces.MediaFilter(
                    TagIds: [.. _hiddenTagIds], PageSize: 100_000), ct);
            set.UnionWith(items.Select(i => i.Id));
        }

        ExcludedMediaIds = set;
    }

    private Task SaveAlbumIdsAsync(CancellationToken ct) =>
        _settings.SetAsync(KeyAlbums, JsonSerializer.Serialize(_hiddenAlbumIds), ct);

    private Task SaveTagIdsAsync(CancellationToken ct) =>
        _settings.SetAsync(KeyTags, JsonSerializer.Serialize(_hiddenTagIds), ct);

    private static string Sha256Hex(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
