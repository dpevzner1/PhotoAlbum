namespace PhotoAlbum.Data.Migrations;

internal static class Migration002_IndexedFolders
{
    public const int Version = 2;

    public const string Sql = """
        -- ── Indexed source folders ───────────────────────────────────────────────────
        CREATE TABLE IF NOT EXISTS IndexedFolder (
            Id              INTEGER PRIMARY KEY AUTOINCREMENT,
            FolderPath      TEXT    NOT NULL UNIQUE,
            Label           TEXT,
            FirstIndexedUtc TEXT    NOT NULL DEFAULT(datetime('now')),
            LastIndexedUtc  TEXT    NOT NULL DEFAULT(datetime('now'))
        );

        -- ── Link each file location to the folder it came from ───────────────────────
        ALTER TABLE MediaFileLocation ADD COLUMN IndexedFolderId INTEGER
            REFERENCES IndexedFolder(Id) ON DELETE SET NULL;
        """;
}
