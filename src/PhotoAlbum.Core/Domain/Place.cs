namespace PhotoAlbum.Core.Domain;

public sealed class Place
{
    public long Id { get; init; }
    public string Name { get; set; } = "";
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public double? Radius { get; set; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
}
