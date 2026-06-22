namespace PhotoAlbum.Core.Interfaces;

public interface IRustThumbnailer
{
    /// <summary>Generate a JPEG thumbnail and return raw bytes.</summary>
    Task<byte[]> GenerateThumbnailAsync(string filePath, int targetSizePx, CancellationToken ct = default);
}
