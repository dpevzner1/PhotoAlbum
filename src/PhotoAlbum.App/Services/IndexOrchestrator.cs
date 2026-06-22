using System.IO;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using PhotoAlbum.Core.Domain;
using PhotoAlbum.Core.Interfaces;

namespace PhotoAlbum.App.Services;

public sealed class IndexProgress
{
    public int Total { get; set; }
    public int Processed { get; set; }
    public int Added { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public string? CurrentFile { get; set; }
}

/// <summary>
/// Scans a folder, hashes each file, and upserts MediaItems into the database.
/// Emits progress via IProgress&lt;IndexProgress&gt;.
/// </summary>
public sealed class IndexOrchestrator
{
    private readonly IRustScanner _scanner;
    private readonly IRustHasher _hasher;
    private readonly IMediaItemRepository _mediaRepo;
    private readonly ThumbnailManager _thumbs;
    private readonly ITagRepository _tagRepo;
    private readonly IIndexedFolderRepository _folderRepo;
    private readonly IOperationLogRepository _opLog;
    private readonly ILogger<IndexOrchestrator> _log;

    public IndexOrchestrator(
        IRustScanner scanner,
        IRustHasher hasher,
        IMediaItemRepository mediaRepo,
        ThumbnailManager thumbs,
        ITagRepository tagRepo,
        IIndexedFolderRepository folderRepo,
        IOperationLogRepository opLog,
        ILogger<IndexOrchestrator> log)
    {
        _scanner = scanner;
        _hasher = hasher;
        _mediaRepo = mediaRepo;
        _thumbs = thumbs;
        _tagRepo = tagRepo;
        _folderRepo = folderRepo;
        _opLog = opLog;
        _log = log;
    }

    public async Task IndexFolderAsync(
        string rootPath,
        IReadOnlyList<long>? batchTagIds = null,
        IProgress<IndexProgress>? progress = null,
        CancellationToken ct = default)
    {
        _log.LogInformation("IndexFolder: scanning {path}", rootPath);

        // Register (or refresh) this folder in the catalog before scanning
        var folderId = await _folderRepo.UpsertAsync(rootPath, ct: ct);

        var files = await _scanner.ScanAsync(rootPath, ct);
        var state = new IndexProgress { Total = files.Count };
        progress?.Report(state);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            state.CurrentFile = file.Path;

            try
            {
                var hash = await _hasher.HashFileAsync(file.Path, ct);
                var existing = await _mediaRepo.GetByHashAsync(hash, ct);

                if (existing is not null)
                {
                    state.Skipped++;
                }
                else
                {
                    var mediaType = IsVideo(file.Extension) ? MediaType.Video : MediaType.Photo;
                    var (lat, lon) = mediaType == MediaType.Photo
                        ? TryReadGps(file.Path)
                        : (null, null);
                    var item = new MediaItem
                    {
                        Blake3Hash   = hash,
                        OriginalName = Path.GetFileName(file.Path),
                        MediaType    = mediaType,
                        Latitude     = lat,
                        Longitude    = lon,
                    };
                    var id = await _mediaRepo.InsertAsync(item, ct);
                    await _thumbs.EnsureThumbnailAsync(id, file.Path, ct: ct);
                    await _mediaRepo.InsertFileLocationAsync(id, file.Path, file.SizeBytes, folderId, ct);
                    await _opLog.LogAsync("Import", "MediaItem", id, file.Path, ct);
                    if (batchTagIds is { Count: > 0 })
                        foreach (var tagId in batchTagIds)
                            await _tagRepo.TagMediaAsync(id, tagId, ct);
                    state.Added++;
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to index {file}", file.Path);
                state.Failed++;
            }

            state.Processed++;
            progress?.Report(state);
        }

        _log.LogInformation("IndexFolder: done. Added={a} Skipped={s} Failed={f}",
            state.Added, state.Skipped, state.Failed);
    }

    private static bool IsVideo(string ext) =>
        ext is "mp4" or "mov" or "m4v";

    /// <summary>
    /// Try to extract GPS coordinates from EXIF metadata using WPF BitmapMetadata.
    /// Returns (null, null) if the file has no GPS data or cannot be read.
    /// </summary>
    private (double? Lat, double? Lon) TryReadGps(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream,
                BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (decoder.Frames.Count == 0) return (null, null);
            if (decoder.Frames[0].Metadata is not BitmapMetadata meta) return (null, null);

            // EXIF GPS latitude: /app1/ifd/gps/subifd:{ulong=2}
            // Each coordinate is stored as 3 rationals (degrees, minutes, seconds)
            var latObj = meta.GetQuery("/app1/ifd/gps/subifd:{ulong=2}");
            var lonObj = meta.GetQuery("/app1/ifd/gps/subifd:{ulong=4}");
            var latRef  = meta.GetQuery("/app1/ifd/gps/subifd:{ulong=1}") as string; // N/S
            var lonRef  = meta.GetQuery("/app1/ifd/gps/subifd:{ulong=3}") as string; // E/W

            if (latObj is ulong[] latArr && lonObj is ulong[] lonArr &&
                latArr.Length == 3 && lonArr.Length == 3)
            {
                double ToDecimal(ulong[] arr)
                {
                    double Rat(ulong r) => (double)(uint)(r >> 32) / Math.Max(1, (uint)(r & 0xFFFFFFFF));
                    return Rat(arr[0]) + Rat(arr[1]) / 60.0 + Rat(arr[2]) / 3600.0;
                }

                var lat = ToDecimal(latArr);
                var lon = ToDecimal(lonArr);
                if (latRef == "S") lat = -lat;
                if (lonRef == "W") lon = -lon;
                return (lat, lon);
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "GPS read failed for {path}", path);
        }
        return (null, null);
    }
}
