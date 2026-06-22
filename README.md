# PhotoAlbum

A full-featured Windows 10/11 desktop photo library management application — an Apple Photos equivalent built for Windows, using .NET 10, WPF, and a Rust performance core.

**License:** MIT  
**Platform:** Windows 10/11 x64  
**Runtime:** .NET 10 (self-contained)

---

## Overview

PhotoAlbum is a local-first, privacy-preserving photo and video library manager. All data stays on your machine — no cloud accounts, no telemetry, no subscriptions. It indexes your existing folder structures non-destructively, leaving originals in place while building a rich searchable catalog backed by SQLite.

---

## Architecture

The project is organized as a multi-layer solution with a Rust native core for performance-critical operations.

```
PhotoAlbum/
├── src/
│   ├── PhotoAlbum.Core/        # Domain models and interfaces (no dependencies)
│   ├── PhotoAlbum.Data/        # SQLite repositories and migrations
│   ├── PhotoAlbum.App/         # WPF application (UI, ViewModels, Services)
│   └── PhotoAlbum.Api/         # Local REST API (ASP.NET Core minimal API)
├── rust/
│   └── photoalbum_core/        # Rust workspace (FFI DLL)
│       └── crates/
│           ├── ffi/            # C ABI exports — cdylib → photoalbum_core.dll
│           ├── hasher/         # BLAKE3 content hashing
│           ├── scanner/        # walkdir recursive media file scanner
│           ├── thumbnailer/    # libimage thumbnail generation
│           └── copy_engine/    # Verified BLAKE3 copy engine for backup/clone
└── COMPILED/                   # Auto-updated self-contained test harness
```

### Layer responsibilities

| Layer | Purpose | Key dependencies |
|---|---|---|
| `PhotoAlbum.Core` | Domain types, repository interfaces, service contracts | None |
| `PhotoAlbum.Data` | SQLite persistence, versioned migrations, Dapper repositories | Microsoft.Data.Sqlite, Dapper |
| `PhotoAlbum.App` | WPF UI, ViewModels (CommunityToolkit.Mvvm), App Services | ModernWpfUI, LibVLCSharp.WPF |
| `PhotoAlbum.Api` | Local REST API on 127.0.0.1:5150, Swagger/OpenAPI docs | ASP.NET Core minimal API |
| `photoalbum_core.dll` | BLAKE3 hashing, directory scanning, thumbnail generation, copy verification, perceptual hashing | Rust crates: blake3, walkdir, image |

### Cross-language boundary (Rust FFI)

All Rust functions are exported with `#[no_mangle] pub extern "C" fn pa_*` and consumed from C# via `[LibraryImport("photoalbum_core")]` source-generated P/Invoke. The FFI contract is defined in `crates/ffi/src/lib.rs` and wrapped by C# interfaces in `PhotoAlbum.Core/Interfaces/`.

```
C# (LibraryImport) ──► photoalbum_core.dll (cdylib)
IRustHasher          ──► pa_blake3_file_hash
IRustScanner         ──► pa_scan_folder
IRustThumbnailer     ──► pa_generate_thumbnail
IRustCopyEngine      ──► pa_copy_verified
IRustPHasher         ──► pa_phash_file
```

---

## Features

### Library Management
- **Non-destructive indexing** — scans existing folders, never moves or modifies originals
- **BLAKE3 content hashing** — each file is uniquely identified by its content hash, not its path; duplicate imports are detected and skipped automatically
- **Multi-folder library** — import from multiple source folders; each is tracked as an indexed location with file count and total size
- **Batch import tagging** — when importing a folder, optionally apply one or more tags to all imported photos at once (existing tags or new custom tags)
- **Incremental re-index** — re-scanning a folder only processes new files

### Search and Filtering (Library View)
All filters are live — results update immediately with no submit button.

| Filter | Description |
|---|---|
| **Full-text search** | Searches filename and notes fields |
| **Media type** | All / Photos / Videos |
| **Minimum rating** | Any / 1★+ through 5★ |
| **Favorites** | Toggle — show only favorited items |
| **Tag filter** | Multi-tag chip picker; items matching any selected tag are shown |
| **Clear all** | Single button resets all active filters |

### Photo and Video Detail View
- Full-resolution image viewer with smooth scaling
- Video playback via LibVLCSharp (inline) with WMF fallback and system player option
- Keyboard navigation: ← → arrows between photos, Backspace to return to library, Ctrl+F to toggle favorite, Delete to trash
- Star rating (1–5) with single-click clear
- Inline notes editor (saved on focus loss)
- Tag management: add/remove tags per photo
- Person tagging (manual, v1)
- EXIF metadata display: date, dimensions, media type

### Albums
- Create named albums, add photos by double-click or drag
- Album detail view with photo grid
- Smart albums (query-based, schema ready)

### People
- Manual person tagging with region coordinates stored in `MediaPerson`
- Person cards with avatar support

### Places
- Named places with latitude/longitude/radius
- Assign photos to places manually
- **Interactive map panel** (split-pane layout): selecting a place renders an OpenStreetMap tile map in an embedded WebView2; no API key required
- **GPS from EXIF**: latitude and longitude are automatically extracted from image EXIF at import time using WPF `BitmapMetadata`; stored on `MediaItem`
- **Detail view location bar**: if a photo has GPS coordinates, a "Show on Map" button appears in the metadata panel and opens Google Maps in the system browser
- "Open in Browser" button launches the selected place in Google Maps

### Events
- Named events with start/end date range
- Assign photos to events

### Multi-Select and Bulk Operations
- Toggle multi-select mode from the Library toolbar
- Ctrl+A selects all visible items; Escape exits multi-select
- Bulk edit: apply tags, rating, or notes to all selected items simultaneously
- Bulk delete: moves selected items to Trash
- Selection count shown in the accent-colored action bar

### Safe Delete
- **Trash mode** — soft delete; items are hidden from library but recoverable via the Trash view
- **Vault mode** — soft delete with a distinct mode flag (future encryption hook)
- **Permanent delete** — hard delete from Trash view only, with confirmation dialog
- **Restore** — restores trashed items to the library in one click
- **Empty Trash** — permanently deletes all trashed items with a single confirmation

### Clone / Backup
- Source and destination folder selection via native folder picker
- Progress bar with per-file status and file count
- All files verified with BLAKE3 after copy; failed files reported
- Cancellable mid-operation via `CancellationTokenSource`
- Interrupted clones are designed to resume (idempotent copy engine)

### Tagging System
- Tags have a name, optional color, and optional parent (hierarchical tag tree, schema ready)
- Tags are created globally and reused across photos
- Import-time batch tagging: assign tags at import, applied to every new file in the batch
- Per-photo tag management in the detail view
- Tag filtering in the library as a primary search tool

### Settings
- **Theme** — Dark, Light, or System (follows Windows dark/light mode automatically); applies immediately without restart
- **Metadata write-back** — global toggle to enable/disable ExifTool write-back of ratings, tags, and notes to original files; off by default
- **Library Locations** — lists all indexed source folders with file count, total size (formatted B/KB/MB/GB), and last indexed date
- **Local API** — live status indicator (checks connectivity to 127.0.0.1:5150), base URL copy button, one-click Open Docs button (launches Swagger UI in the browser)
- **About** — version, build stack info

### Local REST API
- Binds exclusively to `127.0.0.1:5150` — never exposed on LAN
- Base path: `/api/v1/`
- Swagger/OpenAPI documentation at `/api/docs`
- Endpoints cover: media items (query, get, update), albums (list, get, add/remove media), tags (list, create, apply/remove)
- Non-fatal: API failure does not prevent app startup

### Accessibility
- All interactive controls carry `AutomationProperties.Name`
- Keyboard navigation throughout: Tab order, Enter/Space on list items, arrow key photo browsing
- Screen reader labels on all action buttons and form fields
- Color contrast meets WCAG AA for both Light and Dark themes

### Duplicate Detection

PhotoAlbum uses a two-tier duplicate detection strategy:

**Tier 1 — Exact deduplication (BLAKE3)**
Every imported file is hashed byte-for-byte with BLAKE3. A file that is a literal byte-identical copy (e.g., copy-pasted from a backup) is detected and skipped at import time. No separate scan needed.

**Tier 2 — Perceptual deduplication (dHash)**
Visual near-duplicates — re-exported JPEGs, photos resized or recompressed by another app, or the same scene shot twice — have different byte content but look identical to the eye. The perceptual hash pipeline catches these.

Algorithm (dHash):
1. Resize image to 9×8 grayscale (Lanczos3 filter, executed in the Rust core via the `image` crate)
2. Compare each pixel to its right neighbour across each row: 8 comparisons × 8 rows = 64 bits
3. Store the 64-bit hash (`PHash` column on `MediaItem`)
4. At scan time, load all stored hashes and compute pairwise Hamming distance: `popcount(a XOR b)`
5. Pairs with Hamming distance ≤ 10 (≥ ~84% similar) are flagged as possible duplicates

In C#, Hamming distance is computed with `System.Numerics.BitOperations.PopCount(a ^ b)`.

The **Possible Duplicates** smart album (pinned in the Albums view) triggers the scan on demand. Each pair is shown side-by-side with a similarity percentage and three resolution options: **Keep Left**, **Keep Right**, or **Keep Both** (dismiss the pair without deleting either).

The `PHashService` fills missing hashes in background batches of 50 items before each duplicate scan, so freshly imported photos are automatically included.

### Binary Integrity
- `BinaryManifestService` hashes 5 critical assemblies at startup (app exe, Rust DLL, Core, Data, Api)
- Hashes stored in `BinaryManifest` SQLite table
- On mismatch (binary substitution), a warning is logged; non-fatal
- Run silently in background after DB init to avoid delaying startup

---

## Database Schema

All data is stored in a single SQLite file at `%LOCALAPPDATA%\PhotoAlbum\library.db`.

| Table | Purpose |
|---|---|
| `MediaItem` | Core photo/video record with all metadata fields |
| `MediaFileLocation` | Physical file paths, sizes, volume names; linked to `IndexedFolder` |
| `IndexedFolder` | Source folder registry with first/last indexed timestamps |
| `Album` / `AlbumMedia` | Albums and their photo membership |
| `Tag` / `MediaTag` | Tags and per-photo tag assignments |
| `Person` / `MediaPerson` | People and region-tagged assignments to photos |
| `Place` / `MediaPlace` | Named geographic places |
| `Event` / `MediaEvent` | Named time-range events |
| `Metadata` | Key/value EXIF metadata store per photo |
| `MediaItem.Latitude/Longitude` | GPS decimal degrees stored directly on `MediaItem`; extracted from EXIF at import |
| `ThumbnailCache` | Thumbnail file paths indexed by size |
| `OperationLog` | Audit trail of all create/edit/delete/tag operations |
| `UserSettings` | Key/value app preferences (theme, toggle states) |
| `BinaryManifest` | Content hashes of app binaries for integrity verification |
| `SchemaVersion` | Migration version tracking |

Migrations are versioned and run automatically on startup. WAL mode and foreign key enforcement are enabled on every connection.

---

## Tech Stack

| Component | Technology |
|---|---|
| UI Framework | WPF (.NET 10, `net10.0-windows`) |
| UI Controls | ModernWpfUI 0.9.6 (Fluent Design) |
| MVVM | CommunityToolkit.Mvvm 8.4.2 |
| Database | SQLite via Microsoft.Data.Sqlite 10.0.x + Dapper |
| Local API | ASP.NET Core minimal API + Swagger/OpenAPI |
| Video | LibVLCSharp.WPF 3.10.0 + VideoLAN.LibVLC.Windows |
| Logging | Serilog (file sink, rolling daily, 14-day retention) |
| Hosting | Microsoft.Extensions.Hosting (generic host, DI container) |
| Rust toolchain | Rust 1.96+ stable-x86_64-pc-windows-gnu (MSYS2/MinGW) |
| Content hashing | BLAKE3 (Rust crate) |
| File scanning | walkdir (Rust crate) |
| Image processing | image crate (Rust) for thumbnail generation and perceptual hashing |
| Perceptual hashing | dHash (difference hash) implemented in `dedup` crate; Hamming via `BitOperations.PopCount` in C# |

---

## Building

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Rust stable (GNU toolchain)](https://rustup.rs/) — `rustup target add x86_64-pc-windows-gnu`
- [MSYS2](https://www.msys2.org/) with MinGW-w64 — `pacman -S mingw-w64-x86_64-toolchain`
- MinGW bin path in system PATH: `C:\msys64\mingw64\bin`

### Build

```powershell
# Full build (Rust + C#)
dotnet build src/PhotoAlbum.App/PhotoAlbum.App.csproj

# Skip Rust rebuild (faster, when Rust code unchanged)
dotnet build src/PhotoAlbum.App/PhotoAlbum.App.csproj -p:SkipRustBuild=true

# Full self-contained publish to COMPILED/
dotnet publish src/PhotoAlbum.App/PhotoAlbum.App.csproj
```

Every successful `dotnet build` automatically syncs the compiled output to the `COMPILED/` folder (self-contained win-x64 harness). The first `dotnet publish` bootstraps `COMPILED/` with the full .NET runtime (~460 MB); subsequent builds only sync the changed assemblies.

### Run directly from COMPILED

```powershell
.\COMPILED\PhotoAlbum.App.exe
```

No installation required — the folder is fully self-contained.

---

## Project Structure (detailed)

```
src/PhotoAlbum.Core/
  Domain/          MediaItem, Album, Tag, Person, Place, Event, IndexedFolder
  Interfaces/      IMediaItemRepository, ITagRepository, IRustHasher, ...

src/PhotoAlbum.Data/
  DatabaseContext.cs
  Migrations/      Migration001_InitialSchema, Migration002_IndexedFolders,
                   Migration003_PHash, Migration004_GpsCoords
  Repositories/    MediaItemRepository, TagRepository, AlbumRepository,
                   IndexedFolderRepository, ...

src/PhotoAlbum.App/
  App.xaml.cs            Startup, DI registration, theme management
  Views/
    AppShell             Navigation rail + content frame host
    LibraryView          Virtualized thumbnail grid, filter strip, multi-select
    DetailView           Full-res image/video viewer, metadata panel
    AlbumsView           Album grid
    AlbumDetailView      Photos within an album
    PeopleView           Person cards
    PlacesView           Place list
    EventsView           Event list
    TrashView            Soft-deleted items with restore/purge
    CloneView            Backup/clone operation with progress
    SettingsView         Theme, metadata, library locations, local API
    BulkEditDialog       Batch tag/rating/notes editor
    ImportTagsDialog     Tag picker shown before folder import
  ViewModels/
    LibraryViewModel     Search, filter (type/rating/favorites/tags), multi-select
    DetailViewModel      Photo/video detail, tag operations, navigation context
  Services/
    IndexOrchestrator    Scan → hash → upsert → thumbnail → tag pipeline
    ThumbnailManager     Thumbnail generation coordination
    TagService           Tag CRUD and apply/remove
    AlbumService         Album membership management
    ClonePlannerService  Backup job orchestration
    UndoService          Operation log replay
    UserSettingsService  Theme and preference persistence
    BinaryManifestService  Startup integrity check
    StartupIntegrityService  DB migration + HEIC codec check

src/PhotoAlbum.Api/
  LocalApiHost.cs    ASP.NET Core minimal API host
  Endpoints/         /api/v1/media, /api/v1/albums, /api/v1/tags

rust/photoalbum_core/
  crates/ffi/        C ABI: pa_blake3_file_hash, pa_scan_folder,
                           pa_generate_thumbnail, pa_copy_verified,
                           pa_phash_file
  crates/hasher/     BLAKE3 streaming hasher
  crates/scanner/    walkdir scan returning path + size + extension
  crates/thumbnailer/ image crate resize + JPEG encode
  crates/copy_engine/ block-copy with post-copy hash verification
  crates/dedup/      dHash perceptual hashing + BLAKE3 exact dedup grouping
```

---

## Security Notes

- The local REST API binds exclusively to `127.0.0.1` — it is never accessible from the network
- No cloud sync, no remote telemetry, no external connections
- SQLite database is unencrypted in v1 (stored in user's `%LOCALAPPDATA%`)
- ExifTool metadata write-back is opt-in and off by default
- HEIC codec installation is guided through the Microsoft Store; no third-party codec is bundled

---

## HEIC / HEIF Support

HEIC files require the Microsoft HEIF Image Extensions codec. If the codec is not installed, a dismissible warning banner appears in the Library view with a direct link to the Microsoft Store install page (`ms-windows-store://pdp/?ProductId=9PMMSR1CGPWG`). The app is fully functional for JPEG, PNG, TIFF, WEBP, and video formats without any codec installation.

---

## Contributing

This project is open source under the MIT License. Contributions are welcome.

1. Fork the repository
2. Create a feature branch: `git checkout -b feature/your-feature`
3. Build and test locally using `COMPILED/PhotoAlbum.App.exe`
4. Submit a pull request with a clear description of the change

Please keep pull requests focused — one feature or fix per PR. Accessibility labels (`AutomationProperties.Name`) are required on all new interactive controls. New Rust FFI functions must have a corresponding C# interface and P/Invoke wrapper.

---

## License

MIT License — see [LICENSE](LICENSE) for details.
