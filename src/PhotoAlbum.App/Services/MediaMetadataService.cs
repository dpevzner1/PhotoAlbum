using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.QuickTime;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PhotoAlbum.App.Services;

public sealed record MediaFileMetadata(
    DateTime? CaptureUtc, double? Latitude, double? Longitude, double? DurationSeconds);

/// <summary>
/// Unified capture-metadata reader for photos AND videos, built on
/// MetadataExtractor. Photos: EXIF DateTimeOriginal + GPS IFD. Videos
/// (iPhone .MOV / .MP4): QuickTime creation time, track duration, and Apple's
/// com.apple.quicktime.location.ISO6709 GPS atom (e.g. "+40.7128-074.0060+011.0/").
/// All failures degrade to nulls — metadata must never fail an import.
/// </summary>
public static class MediaMetadataService
{
    // ISO 6709: latitude then longitude, each sign-prefixed; optional altitude; trailing '/'
    private static readonly Regex Iso6709 = new(
        @"^(?<lat>[+-]\d+(?:\.\d+)?)(?<lon>[+-]\d+(?:\.\d+)?)", RegexOptions.Compiled);

    public static MediaFileMetadata Extract(string path)
    {
        try
        {
            var dirs = ImageMetadataReader.ReadMetadata(path);

            DateTime? captured = null;
            double? lat = null, lon = null, duration = null;

            // ── Photos: EXIF ──────────────────────────────────────────────────
            var exif = dirs.OfType<ExifSubIfdDirectory>().FirstOrDefault();
            if (exif is not null &&
                exif.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dtOrig))
                captured = dtOrig;

            var gps = dirs.OfType<GpsDirectory>().FirstOrDefault();
            if (gps?.GetGeoLocation() is { IsZero: false } geo)
            {
                lat = geo.Latitude;
                lon = geo.Longitude;
            }

            // ── Videos: QuickTime/MP4 ─────────────────────────────────────────
            var qtHeader = dirs.OfType<QuickTimeMovieHeaderDirectory>().FirstOrDefault();
            if (qtHeader is not null)
            {
                if (captured is null &&
                    qtHeader.TryGetDateTime(QuickTimeMovieHeaderDirectory.TagCreated, out var qtCreated))
                    captured = qtCreated;
                if (qtHeader.TryGetInt64(QuickTimeMovieHeaderDirectory.TagDuration, out var dur) &&
                    qtHeader.TryGetInt64(QuickTimeMovieHeaderDirectory.TagTimeScale, out var scale) &&
                    scale > 0)
                    duration = (double)dur / scale;
            }

            // Apple GPS atom lives in the QuickTime metadata header directory as
            // an ISO 6709 string. Scan all directories for the tag by name so we
            // work across MetadataExtractor's QuickTime directory variants.
            if (lat is null)
            {
                foreach (var dir in dirs)
                {
                    foreach (var tag in dir.Tags)
                    {
                        if (!tag.Name.Contains("ISO6709", StringComparison.OrdinalIgnoreCase) &&
                            !(tag.Name.Contains("location", StringComparison.OrdinalIgnoreCase) &&
                              dir.Name.Contains("QuickTime", StringComparison.OrdinalIgnoreCase)))
                            continue;
                        var value = tag.Description ?? "";
                        var m = Iso6709.Match(value.Trim());
                        if (m.Success &&
                            double.TryParse(m.Groups["lat"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var la) &&
                            double.TryParse(m.Groups["lon"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var lo))
                        {
                            lat = la; lon = lo;
                            break;
                        }
                    }
                    if (lat is not null) break;
                }
            }

            return new MediaFileMetadata(captured, lat, lon, duration);
        }
        catch
        {
            return new MediaFileMetadata(null, null, null, null);
        }
    }
}
