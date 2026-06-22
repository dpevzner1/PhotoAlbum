using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using PhotoAlbum.Core.Interfaces;
using System.IO;
using System.Text.Json;

namespace PhotoAlbum.App.Services;

public enum ExportScope { All, ByFolder, ByAlbum, ByPerson, ByTag, ByFilter }

public record ExportSpec(
    string DestRoot,
    ExportScope Scope,
    long ScopeId,                              // album/person/tag id; ignored for All/ByFolder/ByFilter
    string? FolderFilter,                      // folder prefix filter when Scope == ByFolder
    bool IncludeAppBinary,
    string DatabasePath,
    IReadOnlyList<long>? FilterAlbumIds   = null,  // ByFilter: albums (union)
    IReadOnlyList<long>? FilterPersonIds  = null,  // ByFilter: people  (union)
    IReadOnlyList<long>? FilterTagIds     = null,  // ByFilter: tags    (union)
    IReadOnlyList<long>? FilterEventIds   = null); // ByFilter: events  (union)

public record ExportProgress(int Total, int Completed, int Failed, string Phase, string? CurrentFile);

public sealed class ExportService
{
    private readonly IRustCopyEngine  _copyEngine;
    private readonly IMediaItemRepository _mediaRepo;
    private readonly IAlbumRepository     _albumRepo;
    private readonly IPersonRepository    _personRepo;
    private readonly ITagRepository       _tagRepo;
    private readonly IEventRepository     _eventRepo;
    private readonly ILogger<ExportService> _log;

    public ExportService(
        IRustCopyEngine copyEngine,
        IMediaItemRepository mediaRepo,
        IAlbumRepository albumRepo,
        IPersonRepository personRepo,
        ITagRepository tagRepo,
        IEventRepository eventRepo,
        ILogger<ExportService> log)
    {
        _copyEngine  = copyEngine;
        _mediaRepo   = mediaRepo;
        _albumRepo   = albumRepo;
        _personRepo  = personRepo;
        _tagRepo     = tagRepo;
        _eventRepo   = eventRepo;
        _log         = log;
    }

    public async Task ExecuteAsync(
        ExportSpec spec,
        IProgress<ExportProgress>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(spec.DestRoot);

        // 1 — resolve (id, filePath) pairs for the selected scope
        progress?.Report(new(0, 0, 0, "Resolving files…", null));
        var pairs = await ResolveFilePathsAsync(spec, ct);
        _log.LogInformation("Export: scope={scope} items={n} dest={dest}",
            spec.Scope, pairs.Count, spec.DestRoot);

        // 2 — copy media files
        var mediaRoot = Path.Combine(spec.DestRoot, "Photos");
        Directory.CreateDirectory(mediaRoot);

        int done = 0, failed = 0;
        var exportedIds = new HashSet<long>(pairs.Count);
        var pathRemap   = new Dictionary<string, string>(pairs.Count);

        foreach (var (id, srcPath) in pairs)
        {
            ct.ThrowIfCancellationRequested();

            if (!File.Exists(srcPath))
            {
                failed++;
                progress?.Report(new(pairs.Count, done, failed, "Copying media…", srcPath));
                continue;
            }

            var rel = BuildRelPath(srcPath);
            var dst = Path.Combine(mediaRoot, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);

            try
            {
                await _copyEngine.CopyVerifiedAsync(new(srcPath, dst), ct);
                exportedIds.Add(id);
                pathRemap[srcPath] = Path.Combine("Photos", rel);
                done++;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Export copy failed: {f}", srcPath);
                failed++;
            }

            progress?.Report(new(pairs.Count, done, failed, "Copying media…", Path.GetFileName(srcPath)));
        }

        // 3 — export filtered catalog
        progress?.Report(new(pairs.Count, done, failed, "Exporting catalog…", null));
        await ExportCatalogAsync(spec, exportedIds, pathRemap, ct);

        // 4 — optionally bundle app binary
        if (spec.IncludeAppBinary)
        {
            progress?.Report(new(pairs.Count, done, failed, "Copying application…", null));
            await CopyAppBinaryAsync(spec.DestRoot, ct);
        }

        // 5 — manifest + README
        WriteManifest(spec, pairs.Count, done, failed);
        WriteReadme(spec.DestRoot, spec.IncludeAppBinary);

        _log.LogInformation("Export done: {done}/{total}, {failed} failed", done, pairs.Count, failed);
    }

    // ── scope resolution ──────────────────────────────────────────────────────

    private async Task<IReadOnlyList<(long Id, string FilePath)>> ResolveFilePathsAsync(
        ExportSpec spec, CancellationToken ct)
    {
        // Get all (id, path) pairs from the DB, then filter as needed
        var all = await _mediaRepo.GetAllFilePathsAsync(ct);

        return spec.Scope switch
        {
            ExportScope.All => all,

            ExportScope.ByFolder => [.. all.Where(p =>
                p.FilePath.StartsWith(spec.FolderFilter ?? "", StringComparison.OrdinalIgnoreCase))],

            ExportScope.ByAlbum => await FilterByIdsAsync(all,
                await _albumRepo.GetMediaIdsAsync(spec.ScopeId, ct)),

            ExportScope.ByPerson => await FilterByIdsAsync(all,
                await _personRepo.GetMediaIdsAsync(spec.ScopeId, ct)),

            ExportScope.ByTag => await FilterByTagAsync(spec.ScopeId, all, ct),

            ExportScope.ByFilter => await FilterByMultiAsync(spec, all, ct),

            _ => []
        };
    }

    private static Task<IReadOnlyList<(long Id, string FilePath)>> FilterByIdsAsync(
        IReadOnlyList<(long Id, string FilePath)> all, IReadOnlyList<long> ids)
    {
        var set = ids.ToHashSet();
        IReadOnlyList<(long, string)> result = [.. all.Where(p => set.Contains(p.Id))];
        return Task.FromResult(result);
    }

    private async Task<IReadOnlyList<(long Id, string FilePath)>> FilterByTagAsync(
        long tagId, IReadOnlyList<(long Id, string FilePath)> all, CancellationToken ct)
    {
        // QueryAsync supports tag filter natively
        var items = await _mediaRepo.QueryAsync(
            new Core.Interfaces.MediaFilter(TagIds: [tagId], PageSize: 100_000), ct);
        var set = items.Select(i => i.Id).ToHashSet();
        return [.. all.Where(p => set.Contains(p.Id))];
    }

    private async Task<IReadOnlyList<(long Id, string FilePath)>> FilterByMultiAsync(
        ExportSpec spec, IReadOnlyList<(long Id, string FilePath)> all, CancellationToken ct)
    {
        var matched = new HashSet<long>();

        if (spec.FilterAlbumIds is { Count: > 0 })
            foreach (var id in spec.FilterAlbumIds)
                matched.UnionWith(await _albumRepo.GetMediaIdsAsync(id, ct));

        if (spec.FilterPersonIds is { Count: > 0 })
            foreach (var id in spec.FilterPersonIds)
                matched.UnionWith(await _personRepo.GetMediaIdsAsync(id, ct));

        if (spec.FilterTagIds is { Count: > 0 })
        {
            var items = await _mediaRepo.QueryAsync(
                new Core.Interfaces.MediaFilter(TagIds: [.. spec.FilterTagIds], PageSize: 100_000), ct);
            matched.UnionWith(items.Select(i => i.Id));
        }

        if (spec.FilterEventIds is { Count: > 0 })
            foreach (var id in spec.FilterEventIds)
                matched.UnionWith(await _eventRepo.GetMediaIdsAsync(id, ct));

        return [.. all.Where(p => matched.Contains(p.Id))];
    }

    // ── catalog export ────────────────────────────────────────────────────────

    private async Task ExportCatalogAsync(
        ExportSpec spec,
        HashSet<long> exportedIds,
        Dictionary<string, string> pathRemap,
        CancellationToken ct)
    {
        var destDb = Path.Combine(spec.DestRoot, "catalog.db");
        if (File.Exists(destDb)) File.Delete(destDb);
        File.Copy(spec.DatabasePath, destDb);

        await using var conn = new SqliteConnection($"Data Source={destDb}");
        await conn.OpenAsync(ct);

        // Rewrite absolute file paths to portable relative form
        foreach (var (src, rel) in pathRemap)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE MediaFileLocation SET FilePath=@rel WHERE FilePath=@src";
            cmd.Parameters.AddWithValue("@rel", rel);
            cmd.Parameters.AddWithValue("@src", src);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Remove items not included in this export
        if (exportedIds.Count > 0)
        {
            var ids = string.Join(",", exportedIds);
            await using var del = conn.CreateCommand();
            del.CommandText = $"DELETE FROM MediaItem WHERE Id NOT IN ({ids})";
            await del.ExecuteNonQueryAsync(ct);
        }

        // Cascade-delete orphaned junction rows and compact
        await using var cascade = conn.CreateCommand();
        cascade.CommandText = """
            DELETE FROM MediaFileLocation WHERE MediaItemId NOT IN (SELECT Id FROM MediaItem);
            DELETE FROM MediaTag          WHERE MediaItemId NOT IN (SELECT Id FROM MediaItem);
            DELETE FROM MediaPerson       WHERE MediaItemId NOT IN (SELECT Id FROM MediaItem);
            DELETE FROM MediaPlace        WHERE MediaItemId NOT IN (SELECT Id FROM MediaItem);
            DELETE FROM AlbumMedia        WHERE MediaItemId NOT IN (SELECT Id FROM MediaItem);
            VACUUM;
            """;
        await cascade.ExecuteNonQueryAsync(ct);
    }

    // ── app binary bundling ───────────────────────────────────────────────────

    private static async Task CopyAppBinaryAsync(string destRoot, CancellationToken ct)
    {
        var appDir  = AppContext.BaseDirectory;
        var binDest = Path.Combine(destRoot, "PhotoAlbum");
        Directory.CreateDirectory(binDest);

        foreach (var file in Directory.EnumerateFiles(appDir, "*", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            var dst = Path.Combine(binDest, Path.GetFileName(file));
            try { File.Copy(file, dst, overwrite: true); }
            catch { /* skip locked files (e.g., running exe) */ }
        }

        await Task.CompletedTask;
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static string BuildRelPath(string absolutePath)
    {
        var root = Path.GetPathRoot(absolutePath);
        if (root is not null && absolutePath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            var drive = root.TrimEnd('\\', '/').TrimEnd(':');
            var rest  = absolutePath[root.Length..];
            return Path.Combine(drive, rest);
        }
        return Path.GetFileName(absolutePath);
    }

    private static void WriteManifest(ExportSpec spec, int total, int done, int failed)
    {
        var manifest = new
        {
            ExportedAt   = DateTime.UtcNow.ToString("O"),
            Machine      = Environment.MachineName,
            Scope        = spec.Scope.ToString(),
            ScopeId      = spec.ScopeId,
            FolderFilter = spec.FolderFilter,
            TotalMedia   = total,
            Copied       = done,
            Failed       = failed,
            AppBundled   = spec.IncludeAppBinary,
            AppVersion   = System.Reflection.Assembly.GetExecutingAssembly()
                               .GetName().Version?.ToString() ?? "unknown"
        };

        File.WriteAllText(
            Path.Combine(spec.DestRoot, "export_manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteReadme(string destRoot, bool appBundled)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Photo Album — Export Package");
        sb.AppendLine("============================");
        sb.AppendLine();
        sb.AppendLine("Contents:");
        sb.AppendLine("  Photos/               — exported media files");
        sb.AppendLine("  catalog.db            — SQLite catalog (filtered to exported items)");
        sb.AppendLine("  export_manifest.json  — export metadata");

        if (appBundled)
        {
            sb.AppendLine("  PhotoAlbum/           — application binaries");
            sb.AppendLine();
            sb.AppendLine("To use on another machine:");
            sb.AppendLine("  1. Copy this entire folder to the destination machine.");
            sb.AppendLine("  2. Run PhotoAlbum\\PhotoAlbum.App.exe");
            sb.AppendLine("  3. The app will prompt to open a catalog — select catalog.db");
            sb.AppendLine("     from this export folder, or copy it to:");
            sb.AppendLine("     %LOCALAPPDATA%\\PhotoAlbum\\album.db before first launch.");
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("To use on another machine:");
            sb.AppendLine("  Install Photo Album, then open catalog.db via File > Open Catalog.");
        }

        File.WriteAllText(Path.Combine(destRoot, "README.txt"), sb.ToString());
    }
}
