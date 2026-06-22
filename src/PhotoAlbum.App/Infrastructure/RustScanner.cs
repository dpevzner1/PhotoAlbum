using System.Text.Json;
using PhotoAlbum.Core.Interfaces;

namespace PhotoAlbum.App.Infrastructure;

public sealed class RustScanner : IRustScanner
{
    public Task<IReadOnlyList<ScannedFile>> ScanAsync(string rootPath, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var json = RustInterop.CallWithStringBuffer(
                (buf, len) => RustInterop.pa_scan_directory(rootPath, buf, len),
                initialSize: 64 * 1024);

            var items = JsonSerializer.Deserialize<ScannedFileDto[]>(json) ?? [];
            return (IReadOnlyList<ScannedFile>)items
                .Select(d => new ScannedFile(d.path, d.size_bytes, d.extension))
                .ToList();
        }, ct);
    }

    private record ScannedFileDto(string path, long size_bytes, string extension);
}
