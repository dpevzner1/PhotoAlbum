namespace PhotoAlbum.Core.Domain;

public sealed class Album
{
    public long Id { get; init; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public long? CoverItemId { get; set; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public int SortOrder { get; set; }
    public bool IsSmartAlbum { get; set; }
    public string? SmartQuery { get; set; }
}
