namespace PhotoAlbum.Core.Domain;

public sealed class Tag
{
    public long Id { get; init; }
    public string Name { get; set; } = "";
    public string? Color { get; set; }
    public long? ParentId { get; set; }
}
