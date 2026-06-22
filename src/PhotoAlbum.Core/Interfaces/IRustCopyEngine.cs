namespace PhotoAlbum.Core.Interfaces;

public record CopyJob(string SourcePath, string DestPath);

public interface IRustCopyEngine
{
    /// <summary>Copy a file and verify its BLAKE3 hash post-copy.</summary>
    Task CopyVerifiedAsync(CopyJob job, CancellationToken ct = default);
}
