namespace PhotoAlbum.Core.Domain;

public enum MediaType { Photo, Video, Unknown }
public enum DeleteMode { Trash, Vault, Permanent }

public sealed class MediaItem
{
    public long Id { get; init; }
    public string Blake3Hash { get; init; } = "";
    public string OriginalName { get; set; } = "";
    public int? Width { get; set; }
    public int? Height { get; set; }
    public double? DurationSeconds { get; set; }
    public MediaType MediaType { get; set; }
    public DateTime? CaptureUtc { get; set; }
    public DateTime ImportedUtc { get; init; } = DateTime.UtcNow;
    public int Rating { get; set; }
    public bool IsFavorite { get; set; }
    public bool IsHidden { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedUtc { get; set; }
    public DeleteMode? DeleteMode { get; set; }
    public string? ThumbnailPath { get; set; }
    public string? Notes { get; set; }

    /// <summary>GPS latitude in decimal degrees (positive = North). Null if not present in EXIF.</summary>
    public double? Latitude { get; set; }
    /// <summary>GPS longitude in decimal degrees (positive = East). Null if not present in EXIF.</summary>
    public double? Longitude { get; set; }

    /// <summary>User-applied clockwise rotation in degrees (0, 90, 180, 270). Stored in DB only.</summary>
    public int RotationDegrees { get; set; }
}
