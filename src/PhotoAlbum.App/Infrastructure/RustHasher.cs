using PhotoAlbum.Core.Interfaces;

namespace PhotoAlbum.App.Infrastructure;

public sealed class RustHasher : IRustHasher
{
    public Task<string> HashFileAsync(string filePath, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return RustInterop.CallWithStringBuffer(
                (buf, len) => RustInterop.pa_hash_file(filePath, buf, len), 65);
        }, ct);
    }

    public Task<bool> VerifyFileAsync(string filePath, string expectedHex, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var code = RustInterop.pa_verify_file(filePath, expectedHex);
            return code == RustInterop.ERR_OK;
        }, ct);
    }
}
