using PhotoAlbum.Core.Interfaces;

namespace PhotoAlbum.App.Infrastructure;

public sealed class RustCopyEngine : IRustCopyEngine
{
    public Task CopyVerifiedAsync(CopyJob job, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var code = RustInterop.pa_copy_file_verified(job.SourcePath, job.DestPath);
            RustInterop.ThrowOnError(code, $"{job.SourcePath} → {job.DestPath}");
        }, ct);
    }
}
