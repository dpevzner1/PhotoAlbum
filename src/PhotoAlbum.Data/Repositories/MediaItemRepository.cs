using Dapper;
using PhotoAlbum.Core.Domain;
using PhotoAlbum.Core.Interfaces;

namespace PhotoAlbum.Data.Repositories;

public sealed class MediaItemRepository : IMediaItemRepository
{
    private readonly DatabaseContext _db;

    public MediaItemRepository(DatabaseContext db) => _db = db;

    public async Task<MediaItem?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<MediaItemRow>(
            "SELECT * FROM MediaItem WHERE Id = @id", new { id });
        return row?.ToDomain();
    }

    public async Task<MediaItem?> GetByHashAsync(string blake3Hash, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<MediaItemRow>(
            "SELECT * FROM MediaItem WHERE Blake3Hash = @hash", new { hash = blake3Hash });
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<MediaItem>> QueryAsync(MediaFilter filter, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var (sql, param) = BuildQuery(filter, count: false);
        var rows = await conn.QueryAsync<MediaItemRow>(sql, param);
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<long> CountAsync(MediaFilter filter, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var (sql, param) = BuildQuery(filter, count: true);
        return await conn.ExecuteScalarAsync<long>(sql, param);
    }

    public async Task<long> InsertAsync(MediaItem item, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long>("""
            INSERT INTO MediaItem
                (Blake3Hash, OriginalName, Width, Height, DurationSeconds, MediaType,
                 CaptureUtc, ImportedUtc, Rating, IsFavorite, IsHidden, IsDeleted,
                 DeletedUtc, DeleteMode, ThumbnailPath, Notes)
            VALUES
                (@Blake3Hash, @OriginalName, @Width, @Height, @DurationSeconds, @MediaType,
                 @CaptureUtc, @ImportedUtc, @Rating, @IsFavorite, @IsHidden, @IsDeleted,
                 @DeletedUtc, @DeleteMode, @ThumbnailPath, @Notes);
            SELECT last_insert_rowid();
            """, MediaItemRow.FromDomain(item));
    }

    public async Task UpdateAsync(MediaItem item, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync("""
            UPDATE MediaItem SET
                OriginalName    = @OriginalName,
                Width           = @Width,
                Height          = @Height,
                DurationSeconds = @DurationSeconds,
                MediaType       = @MediaType,
                CaptureUtc      = @CaptureUtc,
                Rating          = @Rating,
                IsFavorite      = @IsFavorite,
                IsHidden        = @IsHidden,
                Notes           = @Notes,
                ThumbnailPath   = @ThumbnailPath
            WHERE Id = @Id
            """, MediaItemRow.FromDomain(item));
    }

    public async Task SoftDeleteAsync(long id, DeleteMode mode, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync("""
            UPDATE MediaItem SET IsDeleted=1, DeletedUtc=datetime('now'), DeleteMode=@mode
            WHERE Id=@id
            """, new { id, mode = mode.ToString() });
    }

    public async Task RestoreAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync("""
            UPDATE MediaItem SET IsDeleted=0, DeletedUtc=NULL, DeleteMode=NULL
            WHERE Id=@id
            """, new { id });
    }

    public async Task HardDeleteAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync("DELETE FROM MediaItem WHERE Id=@id", new { id });
    }

    public async Task SetThumbnailPathAsync(long id, string path, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE MediaItem SET ThumbnailPath=@path WHERE Id=@id",
            new { id, path });
    }

    public async Task InsertFileLocationAsync(long mediaItemId, string filePath, long sizeBytes,
        long indexedFolderId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var volume = System.IO.Path.GetPathRoot(filePath) ?? "";
        await conn.ExecuteAsync("""
            INSERT INTO MediaFileLocation
                (MediaItemId, VolumeName, FilePath, SizeBytes, LastSeenUtc, IsPrimary, IndexedFolderId)
            VALUES
                (@mediaItemId, @volume, @filePath, @sizeBytes, datetime('now'), 1, @indexedFolderId)
            ON CONFLICT DO NOTHING;
            """, new { mediaItemId, volume, filePath, sizeBytes, indexedFolderId });
    }

    public async Task<string?> GetPrimaryFilePathAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT FilePath FROM MediaFileLocation WHERE MediaItemId=@id AND IsPrimary=1 LIMIT 1",
            new { id });
    }

    public async Task SetRotationAsync(long id, int degrees, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE MediaItem SET RotationDegrees=@degrees WHERE Id=@id",
            new { id, degrees });
    }

    public async Task SetPHashAsync(long id, ulong pHash, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE MediaItem SET PHash=@pHash WHERE Id=@id",
            new { id, pHash = (long)pHash });
    }

    public async Task<IReadOnlyList<(long Id, string? FilePath)>> GetItemsMissingPHashAsync(
        int batchSize = 50, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<(long Id, string? FilePath)>(
            """
            SELECT m.Id, mfl.FilePath
            FROM MediaItem m
            LEFT JOIN MediaFileLocation mfl ON mfl.MediaItemId = m.Id AND mfl.IsPrimary = 1
            WHERE m.PHash IS NULL AND m.IsDeleted = 0
              AND m.MediaType IN ('Image', 'Unknown')
            LIMIT @batchSize
            """,
            new { batchSize });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<(long Id, ulong PHash)>> GetAllPHashesAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<(long Id, long PHash)>(
            "SELECT Id, PHash FROM MediaItem WHERE PHash IS NOT NULL AND IsDeleted = 0");
        return rows.Select(r => (r.Id, (ulong)r.PHash)).ToList();
    }

    public async Task<IReadOnlyList<(long Id, string FilePath)>> GetAllFilePathsAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<(long Id, string FilePath)>("""
            SELECT m.Id, mfl.FilePath
            FROM MediaItem m
            JOIN MediaFileLocation mfl ON mfl.MediaItemId = m.Id AND mfl.IsPrimary = 1
            WHERE m.IsDeleted = 0
            ORDER BY m.Id
            """);
        return rows.ToList();
    }

    private static (string sql, object param) BuildQuery(MediaFilter f, bool count)
    {
        var conditions = new List<string> { "1=1" };
        var p = new DynamicParameters();

        if (f.OnlyDeleted) conditions.Add("IsDeleted=1");
        else if (!f.IncludeDeleted) conditions.Add("IsDeleted=0");
        if (f.MediaType.HasValue) { conditions.Add("MediaType=@mt"); p.Add("mt", f.MediaType.Value.ToString()); }
        if (f.MinRating.HasValue) { conditions.Add("Rating>=@mr"); p.Add("mr", f.MinRating.Value); }
        if (f.IsFavorite.HasValue) { conditions.Add("IsFavorite=@fav"); p.Add("fav", f.IsFavorite.Value ? 1 : 0); }
        if (f.IsHidden.HasValue) { conditions.Add("IsHidden=@hid"); p.Add("hid", f.IsHidden.Value ? 1 : 0); }
        if (f.CapturedAfter.HasValue) { conditions.Add("CaptureUtc>=@ca"); p.Add("ca", f.CapturedAfter.Value.ToString("o")); }
        if (f.CapturedBefore.HasValue) { conditions.Add("CaptureUtc<=@cb"); p.Add("cb", f.CapturedBefore.Value.ToString("o")); }
        if (!string.IsNullOrWhiteSpace(f.SearchText))
        {
            conditions.Add("(OriginalName LIKE @q OR Notes LIKE @q)");
            p.Add("q", $"%{f.SearchText}%");
        }

        // Year filter: capture year must be one of the selected years (OR semantics)
        if (f.Years is { Count: > 0 })
        {
            var yph = string.Join(",", f.Years.Select((_, i) => $"@yr{i}"));
            for (int i = 0; i < f.Years.Count; i++) p.Add($"yr{i}", f.Years[i]);
            conditions.Add($"CAST(strftime('%Y', CaptureUtc) AS INTEGER) IN ({yph})");
        }

        var where = string.Join(" AND ", conditions);

        // Relationship filters. Within a category, OR semantics (IN list).
        // Across categories, AND semantics (separate INNER JOINs must all match).
        var joins = new List<string>();
        joins.AddIfFilter(f.TagIds,    "MediaTag",    "TagId",    "tid", p);
        joins.AddIfFilter(f.PersonIds, "MediaPerson", "PersonId", "pid", p);
        joins.AddIfFilter(f.PlaceIds,  "MediaPlace",  "PlaceId",  "plid", p);
        joins.AddIfFilter(f.EventIds,  "MediaEvent",  "EventId",  "eid", p);
        joins.AddIfFilter(f.AlbumIds,  "AlbumMedia",  "AlbumId",  "aid", p);

        var from = joins.Count == 0
            ? "FROM MediaItem m"
            : $"FROM MediaItem m {string.Join(" ", joins)}";

        if (count)
            return ($"SELECT COUNT(DISTINCT m.Id) {from} WHERE {where}", p);

        p.Add("offset", f.Page * f.PageSize);
        p.Add("limit", f.PageSize);
        return ($"SELECT DISTINCT m.* {from} WHERE {where} ORDER BY m.CaptureUtc DESC LIMIT @limit OFFSET @offset", p);
    }

    // ─── DTO ──────────────────────────────────────────────────────────────────────
    private sealed class MediaItemRow
    {
        public long Id { get; set; }
        public string Blake3Hash { get; set; } = "";
        public string OriginalName { get; set; } = "";
        public int? Width { get; set; }
        public int? Height { get; set; }
        public double? DurationSeconds { get; set; }
        public string MediaType { get; set; } = "Unknown";
        public string? CaptureUtc { get; set; }
        public string ImportedUtc { get; set; } = "";
        public int Rating { get; set; }
        public int IsFavorite { get; set; }
        public int IsHidden { get; set; }
        public int IsDeleted { get; set; }
        public string? DeletedUtc { get; set; }
        public string? DeleteMode { get; set; }
        public string? ThumbnailPath { get; set; }
        public string? Notes { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public int RotationDegrees { get; set; }

        public MediaItem ToDomain() => new()
        {
            Id = Id,
            Blake3Hash = Blake3Hash,
            OriginalName = OriginalName,
            Width = Width,
            Height = Height,
            DurationSeconds = DurationSeconds,
            MediaType = Enum.TryParse<Core.Domain.MediaType>(MediaType, out var mt) ? mt : Core.Domain.MediaType.Unknown,
            CaptureUtc = ParseUtc(CaptureUtc),
            ImportedUtc = ParseUtc(ImportedUtc) ?? DateTime.UtcNow,
            Rating = Rating,
            IsFavorite = IsFavorite != 0,
            IsHidden = IsHidden != 0,
            IsDeleted = IsDeleted != 0,
            DeletedUtc = ParseUtc(DeletedUtc),
            DeleteMode = Enum.TryParse<Core.Domain.DeleteMode>(DeleteMode, out var dm) ? dm : null,
            ThumbnailPath = ThumbnailPath,
            Notes = Notes,
            Latitude        = Latitude,
            Longitude       = Longitude,
            RotationDegrees = RotationDegrees,
        };

        public static MediaItemRow FromDomain(MediaItem m) => new()
        {
            Id = m.Id,
            Blake3Hash = m.Blake3Hash,
            OriginalName = m.OriginalName,
            Width = m.Width,
            Height = m.Height,
            DurationSeconds = m.DurationSeconds,
            MediaType = m.MediaType.ToString(),
            CaptureUtc = m.CaptureUtc?.ToString("o"),
            ImportedUtc = m.ImportedUtc.ToString("o"),
            Rating = m.Rating,
            IsFavorite = m.IsFavorite ? 1 : 0,
            IsHidden = m.IsHidden ? 1 : 0,
            IsDeleted = m.IsDeleted ? 1 : 0,
            DeletedUtc = m.DeletedUtc?.ToString("o"),
            DeleteMode = m.DeleteMode?.ToString(),
            ThumbnailPath = m.ThumbnailPath,
            Notes = m.Notes,
        };

        private static DateTime? ParseUtc(string? s) =>
            s is null ? null : DateTime.TryParse(s, out var d) ? d.ToUniversalTime() : null;
    }
}

internal static class MediaFilterJoinExtensions
{
    /// <summary>
    /// Appends an INNER JOIN against a junction table when the id list is non-empty,
    /// giving OR-within-category semantics via an IN clause. Each unique alias keeps
    /// multiple categories independent (AND across categories).
    /// </summary>
    public static void AddIfFilter(
        this List<string> joins,
        IReadOnlyList<long>? ids,
        string junctionTable,
        string fkColumn,
        string paramPrefix,
        DynamicParameters p)
    {
        if (ids is not { Count: > 0 }) return;
        var placeholders = string.Join(",", ids.Select((_, i) => $"@{paramPrefix}{i}"));
        for (int i = 0; i < ids.Count; i++) p.Add($"{paramPrefix}{i}", ids[i]);
        var alias = $"_{paramPrefix}";
        joins.Add($"INNER JOIN {junctionTable} {alias} ON {alias}.MediaItemId = m.Id AND {alias}.{fkColumn} IN ({placeholders})");
    }
}
