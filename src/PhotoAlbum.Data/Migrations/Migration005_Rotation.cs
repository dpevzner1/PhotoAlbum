namespace PhotoAlbum.Data.Migrations;

internal static class Migration005_Rotation
{
    public const int Version = 5;

    public const string Sql = """
        ALTER TABLE MediaItem ADD COLUMN RotationDegrees INTEGER NOT NULL DEFAULT 0;
        """;
}
