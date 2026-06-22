namespace PhotoAlbum.Data.Migrations;

internal static class Migration001_InitialSchema
{
    public const int Version = 1;

    public const string Sql = """
        -- ── Media ────────────────────────────────────────────────────────────────────
        CREATE TABLE IF NOT EXISTS MediaItem (
            Id              INTEGER PRIMARY KEY AUTOINCREMENT,
            Blake3Hash      TEXT    NOT NULL UNIQUE,
            OriginalName    TEXT    NOT NULL,
            Width           INTEGER,
            Height          INTEGER,
            DurationSeconds REAL,
            MediaType       TEXT    NOT NULL CHECK(MediaType IN ('Photo','Video','Unknown')),
            CaptureUtc      TEXT,
            ImportedUtc     TEXT    NOT NULL DEFAULT(datetime('now')),
            Rating          INTEGER NOT NULL DEFAULT 0 CHECK(Rating BETWEEN 0 AND 5),
            IsFavorite      INTEGER NOT NULL DEFAULT 0 CHECK(IsFavorite IN (0,1)),
            IsHidden        INTEGER NOT NULL DEFAULT 0 CHECK(IsHidden IN (0,1)),
            IsDeleted       INTEGER NOT NULL DEFAULT 0 CHECK(IsDeleted IN (0,1)),
            DeletedUtc      TEXT,
            DeleteMode      TEXT    CHECK(DeleteMode IN ('Trash','Vault','Permanent') OR DeleteMode IS NULL),
            ThumbnailPath   TEXT,
            Notes           TEXT
        );

        CREATE TABLE IF NOT EXISTS MediaFileLocation (
            Id          INTEGER PRIMARY KEY AUTOINCREMENT,
            MediaItemId INTEGER NOT NULL REFERENCES MediaItem(Id) ON DELETE CASCADE,
            VolumeName  TEXT    NOT NULL,
            FilePath    TEXT    NOT NULL,
            SizeBytes   INTEGER NOT NULL,
            LastSeenUtc TEXT    NOT NULL DEFAULT(datetime('now')),
            IsPrimary   INTEGER NOT NULL DEFAULT 1 CHECK(IsPrimary IN (0,1))
        );
        CREATE INDEX IF NOT EXISTS idx_mfl_mediaid ON MediaFileLocation(MediaItemId);

        -- ── Albums ───────────────────────────────────────────────────────────────────
        CREATE TABLE IF NOT EXISTS Album (
            Id          INTEGER PRIMARY KEY AUTOINCREMENT,
            Name        TEXT    NOT NULL,
            Description TEXT,
            CoverItemId INTEGER REFERENCES MediaItem(Id) ON DELETE SET NULL,
            CreatedUtc  TEXT    NOT NULL DEFAULT(datetime('now')),
            SortOrder   INTEGER NOT NULL DEFAULT 0,
            IsSmartAlbum INTEGER NOT NULL DEFAULT 0 CHECK(IsSmartAlbum IN (0,1)),
            SmartQuery  TEXT
        );

        CREATE TABLE IF NOT EXISTS AlbumMedia (
            AlbumId     INTEGER NOT NULL REFERENCES Album(Id) ON DELETE CASCADE,
            MediaItemId INTEGER NOT NULL REFERENCES MediaItem(Id) ON DELETE CASCADE,
            Position    INTEGER NOT NULL DEFAULT 0,
            AddedUtc    TEXT    NOT NULL DEFAULT(datetime('now')),
            PRIMARY KEY(AlbumId, MediaItemId)
        );
        CREATE INDEX IF NOT EXISTS idx_albummedia_media ON AlbumMedia(MediaItemId);

        -- ── Tags ─────────────────────────────────────────────────────────────────────
        CREATE TABLE IF NOT EXISTS Tag (
            Id       INTEGER PRIMARY KEY AUTOINCREMENT,
            Name     TEXT    NOT NULL UNIQUE COLLATE NOCASE,
            Color    TEXT,
            ParentId INTEGER REFERENCES Tag(Id) ON DELETE SET NULL
        );

        CREATE TABLE IF NOT EXISTS MediaTag (
            MediaItemId INTEGER NOT NULL REFERENCES MediaItem(Id) ON DELETE CASCADE,
            TagId       INTEGER NOT NULL REFERENCES Tag(Id) ON DELETE CASCADE,
            TaggedUtc   TEXT    NOT NULL DEFAULT(datetime('now')),
            PRIMARY KEY(MediaItemId, TagId)
        );

        -- ── People ───────────────────────────────────────────────────────────────────
        CREATE TABLE IF NOT EXISTS Person (
            Id          INTEGER PRIMARY KEY AUTOINCREMENT,
            Name        TEXT    NOT NULL,
            Notes       TEXT,
            AvatarPath  TEXT,
            CreatedUtc  TEXT    NOT NULL DEFAULT(datetime('now'))
        );

        CREATE TABLE IF NOT EXISTS MediaPerson (
            MediaItemId INTEGER NOT NULL REFERENCES MediaItem(Id) ON DELETE CASCADE,
            PersonId    INTEGER NOT NULL REFERENCES Person(Id) ON DELETE CASCADE,
            RegionX     REAL,
            RegionY     REAL,
            RegionW     REAL,
            RegionH     REAL,
            TaggedUtc   TEXT    NOT NULL DEFAULT(datetime('now')),
            PRIMARY KEY(MediaItemId, PersonId)
        );

        -- ── Places ───────────────────────────────────────────────────────────────────
        CREATE TABLE IF NOT EXISTS Place (
            Id          INTEGER PRIMARY KEY AUTOINCREMENT,
            Name        TEXT    NOT NULL,
            Latitude    REAL,
            Longitude   REAL,
            Radius      REAL,
            CreatedUtc  TEXT    NOT NULL DEFAULT(datetime('now'))
        );

        CREATE TABLE IF NOT EXISTS MediaPlace (
            MediaItemId INTEGER NOT NULL REFERENCES MediaItem(Id) ON DELETE CASCADE,
            PlaceId     INTEGER NOT NULL REFERENCES Place(Id) ON DELETE CASCADE,
            AssignedUtc TEXT    NOT NULL DEFAULT(datetime('now')),
            PRIMARY KEY(MediaItemId, PlaceId)
        );

        -- ── Events ───────────────────────────────────────────────────────────────────
        CREATE TABLE IF NOT EXISTS Event (
            Id          INTEGER PRIMARY KEY AUTOINCREMENT,
            Name        TEXT    NOT NULL,
            StartUtc    TEXT,
            EndUtc      TEXT,
            Description TEXT,
            CreatedUtc  TEXT    NOT NULL DEFAULT(datetime('now'))
        );

        CREATE TABLE IF NOT EXISTS MediaEvent (
            MediaItemId INTEGER NOT NULL REFERENCES MediaItem(Id) ON DELETE CASCADE,
            EventId     INTEGER NOT NULL REFERENCES Event(Id) ON DELETE CASCADE,
            AssignedUtc TEXT    NOT NULL DEFAULT(datetime('now')),
            PRIMARY KEY(MediaItemId, EventId)
        );

        -- ── Metadata ─────────────────────────────────────────────────────────────────
        CREATE TABLE IF NOT EXISTS Metadata (
            MediaItemId INTEGER NOT NULL REFERENCES MediaItem(Id) ON DELETE CASCADE,
            Key         TEXT    NOT NULL,
            Value       TEXT,
            Source      TEXT    NOT NULL DEFAULT 'Exif',
            UpdatedUtc  TEXT    NOT NULL DEFAULT(datetime('now')),
            PRIMARY KEY(MediaItemId, Key)
        );

        -- ── Thumbnails ────────────────────────────────────────────────────────────────
        CREATE TABLE IF NOT EXISTS ThumbnailCache (
            MediaItemId  INTEGER NOT NULL REFERENCES MediaItem(Id) ON DELETE CASCADE,
            SizePx       INTEGER NOT NULL,
            FilePath     TEXT    NOT NULL,
            GeneratedUtc TEXT    NOT NULL DEFAULT(datetime('now')),
            PRIMARY KEY(MediaItemId, SizePx)
        );

        -- ── Operations ────────────────────────────────────────────────────────────────
        CREATE TABLE IF NOT EXISTS OperationLog (
            Id            INTEGER PRIMARY KEY AUTOINCREMENT,
            OperationType TEXT    NOT NULL,
            EntityType    TEXT    NOT NULL,
            EntityId      INTEGER,
            Payload       TEXT,
            OccurredUtc   TEXT    NOT NULL DEFAULT(datetime('now')),
            IsUndone      INTEGER NOT NULL DEFAULT 0 CHECK(IsUndone IN (0,1)),
            UndoneUtc     TEXT
        );
        CREATE INDEX IF NOT EXISTS idx_oplog_occurred ON OperationLog(OccurredUtc DESC);

        -- ── Settings ─────────────────────────────────────────────────────────────────
        CREATE TABLE IF NOT EXISTS UserSettings (
            Key         TEXT    PRIMARY KEY,
            Value       TEXT,
            UpdatedUtc  TEXT    NOT NULL DEFAULT(datetime('now'))
        );

        -- ── Binary Manifest ──────────────────────────────────────────────────────────
        CREATE TABLE IF NOT EXISTS BinaryManifest (
            FileName    TEXT    PRIMARY KEY,
            Blake3Hash  TEXT    NOT NULL,
            SizeBytes   INTEGER NOT NULL,
            VerifiedUtc TEXT    NOT NULL DEFAULT(datetime('now'))
        );
        """;
}
