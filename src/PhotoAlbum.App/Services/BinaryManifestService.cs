using Dapper;
using Microsoft.Extensions.Logging;
using PhotoAlbum.Core.Interfaces;
using PhotoAlbum.Data;
using System.IO;

namespace PhotoAlbum.App.Services;

/// <summary>
/// Records BLAKE3 hashes of critical app binaries in the BinaryManifest table on first run.
/// On subsequent runs, verifies those hashes haven't changed (tamper detection).
/// Non-fatal: logs a warning if a binary is missing or modified, but does not crash the app.
/// </summary>
public sealed class BinaryManifestService
{
    private static readonly string[] CriticalFiles =
    [
        "PhotoAlbum.App.exe",
        "photoalbum_core.dll",
        "PhotoAlbum.Core.dll",
        "PhotoAlbum.Data.dll",
        "PhotoAlbum.Api.dll",
    ];

    private readonly DatabaseContext _db;
    private readonly IRustHasher _hasher;
    private readonly ILogger<BinaryManifestService> _log;

    public BinaryManifestService(DatabaseContext db, IRustHasher hasher, ILogger<BinaryManifestService> log)
    {
        _db = db;
        _hasher = hasher;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var appDir = AppContext.BaseDirectory;

        using var conn = await _db.OpenAsync(ct);

        foreach (var fileName in CriticalFiles)
        {
            var fullPath = Path.Combine(appDir, fileName);
            if (!File.Exists(fullPath))
            {
                _log.LogWarning("BinaryManifest: {file} not found in app directory", fileName);
                continue;
            }

            string currentHash;
            try
            {
                currentHash = await _hasher.HashFileAsync(fullPath, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "BinaryManifest: failed to hash {file}", fileName);
                continue;
            }

            var sizeBytes = new FileInfo(fullPath).Length;
            var existing = await conn.QuerySingleOrDefaultAsync<BinaryManifestRow>(
                "SELECT FileName, Blake3Hash, SizeBytes FROM BinaryManifest WHERE FileName=@fn",
                new { fn = fileName });

            if (existing is null)
            {
                // First run — record the hash
                await conn.ExecuteAsync(
                    """
                    INSERT INTO BinaryManifest (FileName, Blake3Hash, SizeBytes, VerifiedUtc)
                    VALUES (@fn, @hash, @sz, datetime('now'))
                    """,
                    new { fn = fileName, hash = currentHash, sz = sizeBytes });
                _log.LogInformation("BinaryManifest: recorded {file} hash={hash}", fileName, currentHash[..12]);
            }
            else if (existing.Blake3Hash != currentHash)
            {
                _log.LogWarning(
                    "BinaryManifest: HASH MISMATCH for {file}! Expected={expected} Got={got}",
                    fileName, existing.Blake3Hash[..12], currentHash[..12]);
                // Update to new hash so we don't spam warnings on every run after an update
                await conn.ExecuteAsync(
                    """
                    UPDATE BinaryManifest SET Blake3Hash=@hash, SizeBytes=@sz, VerifiedUtc=datetime('now')
                    WHERE FileName=@fn
                    """,
                    new { fn = fileName, hash = currentHash, sz = sizeBytes });
            }
            else
            {
                _log.LogDebug("BinaryManifest: {file} OK", fileName);
                await conn.ExecuteAsync(
                    "UPDATE BinaryManifest SET VerifiedUtc=datetime('now') WHERE FileName=@fn",
                    new { fn = fileName });
            }
        }
    }

    private sealed record BinaryManifestRow(string FileName, string Blake3Hash, long SizeBytes);
}
