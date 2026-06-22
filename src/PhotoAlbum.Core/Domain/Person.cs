namespace PhotoAlbum.Core.Domain;

public sealed class Person
{
    public long Id { get; init; }
    public string Name { get; set; } = "";
    public string? Notes { get; set; }
    public string? AvatarPath { get; set; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
}
