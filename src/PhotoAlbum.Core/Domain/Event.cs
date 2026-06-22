namespace PhotoAlbum.Core.Domain;

public sealed class Event
{
    public long Id { get; init; }
    public string Name { get; set; } = "";
    public DateTime? StartUtc { get; set; }
    public DateTime? EndUtc { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
}
