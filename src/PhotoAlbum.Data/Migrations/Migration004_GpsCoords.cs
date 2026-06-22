namespace PhotoAlbum.Data.Migrations;

internal static class Migration004_GpsCoords
{
    public const int Version = 4;

    public const string Sql = """
        ALTER TABLE MediaItem ADD COLUMN Latitude  REAL;
        ALTER TABLE MediaItem ADD COLUMN Longitude REAL;
        """;
}
