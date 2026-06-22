using Microsoft.Extensions.Logging;
using PhotoAlbum.Core.Interfaces;
using System.IO;

namespace PhotoAlbum.App.Services;

/// <summary>
/// Generates and caches thumbnails on disk.  Skips generation if the file already exists.
/// </summary>
public sealed class ThumbnailManager
{
    private readonly IRustThumbnailer _thumbnailer;
    private readonly IMediaItemRepository _mediaRepo;
    private readonly ILogger<ThumbnailManager> _log;
    private readonly string _cacheRoot;

    public ThumbnailManager(
        IRustThumbnailer thumbnailer,
        IMediaItemRepository mediaRepo,
        ILogger<ThumbnailManager> log)
    {
        _thumbnailer = thumbnailer;
        _mediaRepo = mediaRepo;
        _log = log;
        _cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhotoAlbum", "thumbnails");
        Directory.CreateDirectory(_cacheRoot);
    }

    public async Task<string?> EnsureThumbnailAsync(
        long mediaItemId, string sourcePath, int sizePx = 300,
        CancellationToken ct = default)
    {
        var thumbPath = ThumbPath(mediaItemId, sizePx);
        if (File.Exists(thumbPath))
            return thumbPath;

        try
        {
            var bytes = await _thumbnailer.GenerateThumbnailAsync(sourcePath, sizePx, ct);
            Directory.CreateDirectory(Path.GetDirectoryName(thumbPath)!);
            await File.WriteAllBytesAsync(thumbPath, bytes, ct);
            await _mediaRepo.SetThumbnailPathAsync(mediaItemId, thumbPath, ct);
            return thumbPath;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Thumbnail generation failed for {path}", sourcePath);
            return null;
        }
    }

    private string ThumbPath(long id, int sizePx)
    {
        // Bucket by first 2 hex chars of id to avoid huge flat directories
        var bucket = (id % 256).ToString("x2");
        return Path.Combine(_cacheRoot, bucket, $"{id}_{sizePx}.jpg");
    }
}
