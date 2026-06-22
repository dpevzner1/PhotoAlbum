namespace PhotoAlbum.Core.Interfaces;

public record ScannedFile(string Path, long SizeBytes, string Extension);

public interface IRustScanner
{
    /// <summary>Recursively scan <paramref name="rootPath"/> for supported media files.</summary>
    Task<IReadOnlyList<ScannedFile>> ScanAsync(string rootPath, CancellationToken ct = default);
}
