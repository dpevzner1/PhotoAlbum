namespace PhotoAlbum.Core.Domain;

public sealed class IndexedFolder
{
    public long Id { get; init; }
    public string FolderPath { get; init; } = "";
    public string? Label { get; set; }
    public DateTime FirstIndexedUtc { get; init; }
    public DateTime LastIndexedUtc { get; set; }

    // Aggregates — populated by the repository via JOIN, not stored columns
    public long FileCount { get; set; }
    public long TotalSizeBytes { get; set; }
}
