namespace PhotoAlbum.Core.Interfaces;

public interface IRustPHasher
{
    /// <summary>
    /// Compute a 64-bit dHash (difference hash) for the image file at <paramref name="filePath"/>.
    /// Returns null if the file cannot be decoded (non-image, corrupt, etc.).
    /// </summary>
    Task<ulong?> PHashFileAsync(string filePath, CancellationToken ct = default);
}
