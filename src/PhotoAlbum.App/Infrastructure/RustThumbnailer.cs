using PhotoAlbum.Core.Interfaces;

namespace PhotoAlbum.App.Infrastructure;

public sealed class RustThumbnailer : IRustThumbnailer
{
    private const int MaxThumbBytes = 4 * 1024 * 1024; // 4 MB ceiling per thumbnail

    public Task<byte[]> GenerateThumbnailAsync(string filePath, int targetSizePx, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var buf = new byte[MaxThumbBytes];
            var code = RustInterop.pa_generate_thumbnail(
                filePath, (uint)targetSizePx, buf, (nuint)buf.Length, out var written);
            RustInterop.ThrowOnError(code, filePath);
            var result = new byte[(int)written];
            Array.Copy(buf, result, (int)written);
            return result;
        }, ct);
    }
}
