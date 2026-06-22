using PhotoAlbum.Core.Interfaces;

namespace PhotoAlbum.App.Infrastructure;

public sealed class RustPHasher : IRustPHasher
{
    public Task<ulong?> PHashFileAsync(string filePath, CancellationToken ct = default)
    {
        return Task.Run<ulong?>(() =>
        {
            ct.ThrowIfCancellationRequested();
            var code = RustInterop.pa_phash_file(filePath, out var hash);
            return code == RustInterop.ERR_OK ? hash : null;
        }, ct);
    }
}
