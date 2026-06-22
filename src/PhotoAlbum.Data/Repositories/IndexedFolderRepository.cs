using Dapper;
using PhotoAlbum.Core.Domain;
using PhotoAlbum.Core.Interfaces;

namespace PhotoAlbum.Data.Repositories;

public sealed class IndexedFolderRepository : IIndexedFolderRepository
{
    private readonly DatabaseContext _db;

    public IndexedFolderRepository(DatabaseContext db) => _db = db;

    public async Task<long> UpsertAsync(string folderPath, string? label = null, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long>("""
            INSERT INTO IndexedFolder (FolderPath, Label, FirstIndexedUtc, LastIndexedUtc)
            VALUES (@folderPath, @label, datetime('now'), datetime('now'))
            ON CONFLICT(FolderPath) DO UPDATE SET
                LastIndexedUtc = datetime('now'),
                Label = COALESCE(@label, Label)
            RETURNING Id;
            """, new { folderPath, label });
    }

    public async Task<IReadOnlyList<IndexedFolder>> GetAllAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<IndexedFolderRow>("""
            SELECT
                f.Id, f.FolderPath, f.Label,
                f.FirstIndexedUtc, f.LastIndexedUtc,
                COUNT(mfl.Id)       AS FileCount,
                COALESCE(SUM(mfl.SizeBytes), 0) AS TotalSizeBytes
            FROM IndexedFolder f
            LEFT JOIN MediaFileLocation mfl ON mfl.IndexedFolderId = f.Id
            GROUP BY f.Id
            ORDER BY f.LastIndexedUtc DESC;
            """);
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<IndexedFolder?> GetByPathAsync(string folderPath, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<IndexedFolderRow>("""
            SELECT
                f.Id, f.FolderPath, f.Label,
                f.FirstIndexedUtc, f.LastIndexedUtc,
                COUNT(mfl.Id)       AS FileCount,
                COALESCE(SUM(mfl.SizeBytes), 0) AS TotalSizeBytes
            FROM IndexedFolder f
            LEFT JOIN MediaFileLocation mfl ON mfl.IndexedFolderId = f.Id
            WHERE f.FolderPath = @folderPath
            GROUP BY f.Id;
            """, new { folderPath });
        return row?.ToDomain();
    }

    private sealed class IndexedFolderRow
    {
        public long Id { get; init; }
        public string FolderPath { get; init; } = "";
        public string? Label { get; init; }
        public string FirstIndexedUtc { get; init; } = "";
        public string LastIndexedUtc { get; init; } = "";
        public long FileCount { get; init; }
        public long TotalSizeBytes { get; init; }

        public IndexedFolder ToDomain() => new()
        {
            Id             = Id,
            FolderPath     = FolderPath,
            Label          = Label,
            FirstIndexedUtc = DateTime.Parse(FirstIndexedUtc, null, System.Globalization.DateTimeStyles.AssumeUniversal),
            LastIndexedUtc  = DateTime.Parse(LastIndexedUtc,  null, System.Globalization.DateTimeStyles.AssumeUniversal),
            FileCount      = FileCount,
            TotalSizeBytes = TotalSizeBytes,
        };
    }
}
