using Microsoft.Extensions.Logging;
using PhotoAlbum.Core.Interfaces;
using System.IO;
using System.Text.Json;

namespace PhotoAlbum.App.Services;

public record CloneJob(
    string SourceRoot,
    string DestRoot,
    IReadOnlyList<string> FilePaths);

public record CloneProgress(
    int Total,
    int Completed,
    int Failed,
    string? CurrentFile);

/// <summary>
/// Plans and executes verified clone/backup operations via the Rust copy engine.
/// Writes a JSON manifest so interrupted clones can resume.
/// </summary>
public sealed class ClonePlannerService
{
    private readonly IRustCopyEngine _copyEngine;
    private readonly IRustHasher _hasher;
    private readonly ILogger<ClonePlannerService> _log;

    public ClonePlannerService(
        IRustCopyEngine copyEngine,
        IRustHasher hasher,
        ILogger<ClonePlannerService> log)
    {
        _copyEngine = copyEngine;
        _hasher = hasher;
        _log = log;
    }

    /// <summary>
    /// Clone all files from <paramref name="job"/>.SourceRoot to DestRoot,
    /// preserving relative paths and verifying each copy with BLAKE3.
    /// </summary>
    public async Task ExecuteAsync(
        CloneJob job,
        IProgress<CloneProgress>? progress = null,
        CancellationToken ct = default)
    {
        _log.LogInformation("Clone start: {src} → {dst} ({n} files)",
            job.SourceRoot, job.DestRoot, job.FilePaths.Count);

        var manifestPath = Path.Combine(job.DestRoot, ".clone_manifest.json");
        var completed = LoadManifest(manifestPath);
        int done = 0, failed = 0;

        foreach (var srcFile in job.FilePaths)
        {
            ct.ThrowIfCancellationRequested();

            if (completed.Contains(srcFile))
            {
                done++;
                progress?.Report(new(job.FilePaths.Count, done, failed, srcFile));
                continue;
            }

            var rel = Path.GetRelativePath(job.SourceRoot, srcFile);
            var dst = Path.Combine(job.DestRoot, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);

            try
            {
                await _copyEngine.CopyVerifiedAsync(new(srcFile, dst), ct);
                completed.Add(srcFile);
                done++;
                progress?.Report(new(job.FilePaths.Count, done, failed, srcFile));

                // Save progress every 10 files for resume-on-interrupt
                if (done % 10 == 0)
                    SaveManifest(manifestPath, completed);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Clone failed for {file}", srcFile);
                failed++;
                progress?.Report(new(job.FilePaths.Count, done, failed, srcFile));
            }
        }

        SaveManifest(manifestPath, completed);
        _log.LogInformation("Clone done: {done}/{total} succeeded, {failed} failed",
            done, job.FilePaths.Count, failed);
    }

    private static HashSet<string> LoadManifest(string path)
    {
        if (!File.Exists(path)) return [];
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<HashSet<string>>(json) ?? [];
        }
        catch { return []; }
    }

    private static void SaveManifest(string path, HashSet<string> completed)
    {
        try { File.WriteAllText(path, JsonSerializer.Serialize(completed)); }
        catch { /* non-fatal */ }
    }
}
