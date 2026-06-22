namespace PhotoAlbum.Data.Migrations;

internal static class Migration003_PHash
{
    public const int Version = 3;

    public const string Sql = """
        ALTER TABLE MediaItem ADD COLUMN PHash INTEGER;
        """;
}
