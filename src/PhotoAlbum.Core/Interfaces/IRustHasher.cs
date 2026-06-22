namespace PhotoAlbum.Core.Interfaces;

public interface IRustHasher
{
    /// <summary>Compute BLAKE3 hash of <paramref name="filePath"/>, returning lowercase hex.</summary>
    Task<string> HashFileAsync(string filePath, CancellationToken ct = default);

    /// <summary>Verify <paramref name="filePath"/> matches <paramref name="expectedHex"/>.</summary>
    Task<bool> VerifyFileAsync(string filePath, string expectedHex, CancellationToken ct = default);
}
