namespace PhotoAlbum.Core.Interfaces;

/// <summary>
/// Exposes read/write access to the hidden-content vault for API callers.
/// Implemented by HiddenContentService in the App layer.
/// </summary>
public interface IHiddenContentManager
{
    bool IsUnlocked { get; }
    bool HasPin { get; }
    IReadOnlySet<long> HiddenAlbumIds { get; }
    IReadOnlySet<long> HiddenTagIds { get; }

    /// <summary>Unlock. Pass null/empty when no PIN is set.</summary>
    Task<bool> TryUnlockAsync(string? pin, CancellationToken ct = default);

    void Lock();

    Task HideAlbumAsync(long albumId, CancellationToken ct = default);
    Task UnhideAlbumAsync(long albumId, CancellationToken ct = default);
    Task HideTagAsync(long tagId, CancellationToken ct = default);
    Task UnhideTagAsync(long tagId, CancellationToken ct = default);
}
