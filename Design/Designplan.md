# PhotoAlbum App — Design Plan
*Consulted: DevOps Logic Engine — all primary domains applied*

---

## Target Platform
- Windows 10/11, 64-bit
- Portable `.exe` during development
- Self-contained installer (MSIX or WiX) for production
- Installer checks and installs prerequisites if absent

---

## Core Design Principles

These govern every decision in the plan. Non-negotiable.

### UI Simplicity Mandate
The interface must be **simple, logical, comprehensive, and intuitive**. Every screen must pass this test:

> *"Can a person who has never used this app figure out what to do in under 10 seconds without reading a manual?"*

Rules derived from this:
- One primary action per screen — always obvious, always reachable
- No buried settings that affect visible behavior
- Labels use plain language — no technical terms in the UI
- Every action is reversible or has a clear undo path shown immediately after
- Navigation never traps the user — there is always a way back
- Progressive disclosure: simple by default, detail available on demand
- Empty states always tell the user what to do next — never just "No items found"
- Confirmation dialogs only for destructive or irreversible actions — not for every save

### Clean Architecture Dependency Rule
Dependencies flow inward only:

```
UI Layer  →  App Services  →  Domain / Data Layer
                    ↓
            Rust FFI (via interface)
```

- UI knows about Services; Services do not know about UI
- Services know about the Domain and Data layer; Data layer does not know about Services
- Rust is an external adapter — C# Services call it through an interface (`IScanner`, `IHasher`, etc.), never directly
- Domain logic (tag rules, album membership, delete policy) lives in C# Services, never in UI or Rust

### Error Handling Standard
- Every failure is explicit, actionable, and observable — no silent failures anywhere
- User-facing errors use plain language: "Your library folder couldn't be read. Check that the folder still exists." — not "IOException: Access denied (0x80070005)"
- All errors are logged to the internal diagnostic log
- Recovery action is always offered: retry, skip, open folder, contact support

---

## Technology Stack

### Primary: C# + .NET 9
- **UI:** WinUI 3 (Windows App SDK) — Fluent Design, modern feel
- **Database:** SQLite via Microsoft.Data.Sqlite (unencrypted; accepted for v1 personal desktop use)
- **Metadata:** ExifTool (bundled, hash-verified)
- **Video playback:** LibVLCSharp (libvlc bundled — no VLC install required by user) + Windows Media Foundation fallback via `MediaPlayerElement`
- **Video thumbnails:** FFmpeg (bundled, hash-verified)
- **Image decoding:** Windows Imaging Component + HEIF/HEVC codec (guided install if absent)

### Core Engine: Rust (`photoalbum_core.dll`, via C# P/Invoke FFI)
- File system scanning and recursive indexing
- BLAKE3 content hashing and fingerprinting
- Duplicate detection
- Thumbnail generation pipeline
- HEIC/HEIF raw decoding assist
- Batch file copy and clone engine
- Hash verification during clone validation

### Why this split
C# owns UI, orchestration, database, API, and all user-facing logic.
Rust owns anything that touches large numbers of files or bytes at speed.
The boundary is thin, explicit, and contractually defined (see FFI Contract section).

---

## Rust Cargo Workspace Structure

```
photoalbum_core/           ← Rust workspace root
  Cargo.toml               ← workspace manifest
  crates/
    ffi/                   ← C-compatible exports only; no business logic
      src/lib.rs
    scanner/               ← recursive file discovery
      src/lib.rs
      benches/scan_bench.rs
    hasher/                ← BLAKE3 hashing
      src/lib.rs
      benches/hash_bench.rs
    thumbnailer/           ← thumbnail generation
      src/lib.rs
      benches/thumb_bench.rs
    copy_engine/           ← verified file copy with resume
      src/lib.rs
    verifier/              ← post-clone integrity check
      src/lib.rs
    dedup/                 ← duplicate detection by hash
      src/lib.rs
```

**Benchmarking:** `criterion` benchmarks ship alongside every hot-path crate from day one. Target thresholds (must be documented and enforced in CI):
- `hasher`: ≥ 2 GB/s throughput on NVMe
- `scanner`: ≥ 10,000 files/s discovery rate
- `thumbnailer`: ≥ 50 thumbnails/s at 200px output

**Profiling plan:** `cargo flamegraph` on real library corpus (10,000+ files) before each milestone. Results stored in `docs/perf/`.

---

## Rust FFI Contract

Every function exported by `ffi/src/lib.rs` follows these rules without exception:

### Safety Rules
1. Every FFI entry point is wrapped in `std::panic::catch_unwind` — panics must never cross the boundary (undefined behavior)
2. All return types are `i32` error codes — no Rust types cross the boundary
3. Out-parameters use caller-allocated buffers with explicit length fields
4. Rust never allocates memory that C# must free, and vice versa — ownership is explicit per function

### Error Code Enum (defined in both Rust and C#)
```
0   = OK
1   = InvalidArgument
2   = PathNotFound
3   = PermissionDenied
4   = IoError
5   = HashMismatch
6   = BufferTooSmall
7   = OperationCancelled
99  = InternalError (panic caught)
```

### Representative Signatures
```c
// Scanner
int32_t scan_directory(
    const char* root_path,
    ScanCallback callback,     // called once per discovered file
    void* user_data,
    const char** error_msg_out
);

// Hasher
int32_t hash_file(
    const char* file_path,
    uint8_t* hash_out,         // caller allocates 32 bytes (BLAKE3)
    const char** error_msg_out
);

// Thumbnailer
int32_t generate_thumbnail(
    const char* file_path,
    uint32_t target_size_px,
    uint8_t* buffer_out,
    uint32_t* buffer_len_inout,
    const char** error_msg_out
);

// Copy engine
int32_t copy_files_verified(
    const char* manifest_path,
    CopyProgressCallback callback,
    void* user_data,
    const char** error_msg_out
);
```

### C# Interface Abstractions (enforces testability)
```csharp
public interface IScanner
{
    IAsyncEnumerable<ScannedFile> ScanAsync(string rootPath, CancellationToken ct);
}

public interface IHasher
{
    Task<byte[]> HashFileAsync(string path, CancellationToken ct);
}

public interface IThumbnailer
{
    Task<byte[]> GenerateThumbnailAsync(string path, int sizePx, CancellationToken ct);
}

public interface ICopyEngine
{
    Task<CopyResult> CopyVerifiedAsync(CopyManifest manifest,
        IProgress<CopyProgress> progress, CancellationToken ct);
}
```

The Rust DLL implementations (`RustScanner`, `RustHasher`, etc.) are registered in the DI container. Tests inject fakes through these interfaces.

---

## Architecture Overview

```
PhotoAlbum.exe  (C# / .NET 9 / WinUI 3)
│
├── UI Layer
│   ├── AppShell                   ← adaptive nav + top bar
│   ├── LibraryHomeView
│   ├── MediaGridView              ← responsive auto-column wall
│   ├── PhotoDetailView
│   ├── FilterView
│   ├── AlbumBrowser
│   ├── PeopleBrowser
│   ├── LocationBrowser
│   ├── EventBrowser
│   ├── BulkEditDialog
│   ├── CloneProgressView
│   ├── MetadataPanel              ← adaptive: side / bottom sheet
│   └── DesignSystem/              ← tokens, themes, shared controls
│
├── App Services (C#)
│   ├── IndexOrchestrator          ← calls IScanner, IHasher, IThumbnailer
│   ├── CommitChangeService
│   ├── UndoRollbackService
│   ├── TagService
│   ├── AlbumService
│   ├── ClonePlannerService        ← calls ICopyEngine
│   ├── ApiKeyService
│   ├── LocalApiServer             ← ASP.NET Core minimal API, 127.0.0.1 only
│   ├── DiagnosticLogger           ← structured internal log (Serilog)
│   └── StartupIntegrityService   ← DB check, binary hash verify
│
├── Data Layer (C#)
│   ├── LibraryDb (SQLite)
│   └── ThumbnailCache (file system)
│
└── photoalbum_core.dll  (Rust)
    └── ffi → scanner, hasher, thumbnailer, copy_engine, verifier, dedup
```

---

## Supported File Types
- `.jpg`, `.jpeg`, `.png`
- `.heic`, `.heif`
- `.mov`, `.mp4`, `.m4v`

---

## Design System

The foundational layer. Every view, panel, dialog, and menu inherits from it.

### Fluent Design Foundation (WinUI 3)
- **Materials:** Mica (main window) + Acrylic (panels, flyouts)
- **Depth:** layered z-order — content → panels → dialogs
- **Motion:** implicit animations on layout change; skeleton loaders during async ops; entrance/exit on navigation
- **Theme:** system Light / Dark / High Contrast at startup and live on change

### Design Tokens
```
-- Spacing (4px base unit) --
space-1: 4px  | space-2: 8px  | space-3: 12px | space-4: 16px
space-6: 24px | space-8: 32px | space-12: 48px

-- Typography --
Display:  32px SemiBold  — screen titles
Title:    20px SemiBold  — section headers
Subtitle: 16px SemiBold  — card labels, panel headers
Body:     14px Regular   — body text, metadata values
Caption:  12px Regular   — timestamps, secondary labels
Label:    11px Medium    — badge text, pill labels

-- All sizes scale with system DPI and text size settings --

-- Color roles (resolved per theme) --
surface-base:     window background (Mica)
surface-card:     thumbnail card, panel background
surface-overlay:  dialog scrim, flyout backdrop
accent:           system accent color
accent-subtle:    10% opacity accent — hover, selection ring
text-primary:     primary labels
text-secondary:   metadata values, captions
text-disabled:    inactive labels
border-subtle:    card borders, dividers
danger:           destructive actions
success:          verified, complete
warning:          caution states
```

### Component Library

| Component | Description |
|-----------|-------------|
| `ThumbnailCard` | Hover overlay with quick-actions; selection ring; checkmark on multi-select |
| `MetadataRow` | Label + editable value; inline edit on click; saves on blur |
| `TagPill` | Colored pill + remove button; FlexWrap layout |
| `SectionHeader` | Title + count badge + optional action button |
| `ConfirmDialog` | Centered modal max 560px; keyboard-escapable; destructive actions styled in danger color |
| `ProgressSheet` | Bottom sheet for long ops; cancel button; "run in background" option |
| `EmptyState` | Icon + heading + plain-language subtext + CTA; required on every list/grid |
| `SkeletonLoader` | Shimmer placeholder during async loads — never a blank screen |
| `ToastNotification` | WinUI 3 InfoBar; non-blocking; auto-dismiss after 5s; includes undo link where applicable |
| `BreadcrumbBar` | Current path/context; updates on every nav event |
| `ContextMenu` | Right-click MenuFlyout; consistent action set per item type |

### Interaction States (all five required on every control)
1. **Default** — resting
2. **Hover** — subtle background shift
3. **Pressed** — scale 0.97, slight darken
4. **Focused** — 2px accent focus ring (keyboard / accessibility)
5. **Disabled** — 40% opacity, no pointer events

### Data States (all three required on every async surface)
- **Loading** — SkeletonLoader or ProgressRing; never blank
- **Empty** — EmptyState with context-specific message and next-action CTA
- **Error** — InfoBar (error) with plain-language message and retry action; never silent

---

## User Persona

**Primary: The Family Memory Keeper**

- Manages a personal/family photo archive of 5,000–50,000+ photos accumulated over many years from iPhones, older cameras, and various devices
- Not a professional photographer or developer — comfortable with consumer software (iPhone Photos, Windows Explorer, Google Photos)
- Core jobs-to-be-done:
  1. Find a specific photo quickly ("the one from Viktor's birthday in 2015")
  2. Organize photos into meaningful groups without moving files manually
  3. Know the library is safe and backed up
  4. Share or export a set of photos easily
- Frustrations with current tools: Google Photos requires cloud upload; Windows Photos is basic; Apple Photos is macOS/iPhone only
- Expectation: app behaves like Apple Photos but runs on Windows and works on local files

This persona drives every UI simplicity decision. If the Family Memory Keeper can't find a feature without searching, it needs to be redesigned.

---

## Responsive / Reactive Layout System

### DPI Awareness
- `PerMonitorV2` DPI awareness in app manifest
- All sizing in WinUI 3 layout units (effective pixels) — never raw pixels
- Target DPI scales: 100%, 125%, 150%, 200%
- Images/thumbnails: `BitmapScalingMode.HighQuality` at all scales
- Icons: SVG or multi-scale PNG assets (100/125/150/200%)

### Adaptive Breakpoints

| Breakpoint | Width | Layout Mode |
|------------|-------|-------------|
| Compact | < 640px | Single column; bottom tab bar; no side panel |
| Narrow | 640–900px | Two-column possible; collapsed nav pane |
| Medium | 900–1280px | Side panel 280px; expanded nav pane |
| Wide | > 1280px | Full three-column; nav always visible |

Transitions: 200ms ease-out — no snapping.

### Navigation Shell (AppShell)

```
Wide (>1280px):
┌──────────┬───────────────────────────────┬──────────────┐
│ NAV PANE │  CONTENT AREA                 │ DETAIL PANEL │
│  220px   │  flex                         │  320px       │
└──────────┴───────────────────────────────┴──────────────┘

Medium (900–1280px):
┌──────────┬────────────────────────────────────────────────┐
│ NAV PANE │  CONTENT AREA (detail slides in as overlay)    │
│  220px   │  flex                                          │
└──────────┴────────────────────────────────────────────────┘

Compact (<640px):
┌──────────────────────────────────────────────────────────┐
│  [☰]  Title                              [🔍]  [⋯]      │
├──────────────────────────────────────────────────────────┤
│  CONTENT AREA  (full width)                              │
├──────────────────────────────────────────────────────────┤
│  Library │ Albums │ People │ Places │ Events             │
└──────────────────────────────────────────────────────────┘
```

Navigation items: Library · Albums · People · Places · Events · Settings (bottom of pane)
Always accessible: Search (top bar, Ctrl+F), Sort, View Size

### Thumbnail Grid — Reactive Auto-Column
```
columns = floor(availableWidth / targetThumbnailSize)

Size mode → targetThumbnailSize:
  Small:  120px  |  Medium: 200px (default)  |  Large: 300px

Minimum 2 columns. Gap: 8px. Virtualized (only visible cards rendered).
```

### Metadata Panel — Adaptive
| Mode | Behavior |
|------|----------|
| Wide | Fixed right 320px; always visible when item selected |
| Medium | Slide-in overlay 320px from right; dismissible |
| Compact | Bottom sheet 60% height; snap points; swipe-dismissible |

When nothing is selected: panel shows library summary (count, size, active filter).

### Dialogs — Responsive Sizing
| Dialog | Wide | Compact |
|--------|------|---------|
| Confirm | Centered max 560px | Full-width bottom sheet |
| Bulk edit | Centered max 700px | Full-screen slide-up |
| Clone progress | Centered max 640px | Full-screen |
| Settings | Right panel 480px | Full-screen |
| Context menu | Flyout at cursor | Bottom action sheet |

All dialogs: Tab navigable, Enter confirms, Escape cancels.

---

## UI — Screen Inventory and Interaction Design

### 1. First Launch / System Readiness Check + Onboarding

Before showing the library, `StartupIntegrityService` silently checks system readiness. If anything is missing, a clear non-blocking banner appears — not a blocking dialog.

**Readiness checks (run once on first launch, re-runnable from Settings → About):**

| Check | Pass | Fail action |
|-------|------|-------------|
| HEIC/HEVC codec | Codec present | Show banner: "To view iPhone photos (HEIC), install the free HEVC codec from the Microsoft Store." + [ Install Now ] button that opens Store directly. Dismiss-able — app works for JPEG/PNG/video without it. |
| Video codec readiness | LibVLCSharp loads successfully | Show banner: "Video playback encountered an issue. Some videos may not play." + [ Diagnostics ] link |
| ExifTool binary hash | Hash matches manifest | Block indexing; show: "A required tool is corrupted. Please reinstall the app." |
| FFmpeg binary hash | Hash matches manifest | Block video thumbnails; show warning; image indexing continues |
| SQLite integrity | `PRAGMA integrity_check` passes | Show recovery dialog (see Error Handling section) |

After checks pass (or banners are shown), onboarding proceeds:
- Full-screen welcome — app name, one-sentence description
- Single CTA: **"Choose your photo folder"**
- Folder picker → path confirmation → indexing begins immediately
- No wizard, no settings, no account — one action to start
- Progress shown as non-blocking ProgressSheet (dismissible)

### 2. Indexing Progress
Non-blocking ProgressSheet at bottom of screen:
```
  Indexing your library
  ████████░░░░  2,847 / 18,420 files
  Currently: /photos/trips/2022/...
  Estimated: ~4 min remaining
                          [ Run in background ]  [✕]
```
- User can browse already-indexed items while indexing continues
- Status badge in nav bar shows progress when sheet is dismissed
- On complete: toast "Library ready — 18,420 items indexed"

### 3. Library Home — View All

```
TOP BAR:
[ PhotoAlbum > Library ]  [🔍 Search]  [ Sort: Date ▾ ]  [ ⊞ Medium ▾ ]  [ ⋯ ]

LEFT NAV:
  📷  Library          ← active
  🗂  Albums
  👤  People
  📍  Places
  🎉  Events
  ─────────────
  ⚙  Settings

CONTENT — responsive thumbnail grid (virtualized):
  [ card ][ card ][ card ][ card ][ card ][ card ]
  [ card ][ card ][ card ][ card ][ card ][ card ]
  ...

RIGHT PANEL (wide, when item selected):
  ┌──────────────────────────┐
  │  [preview]               │
  │  IMG_4821.heic           │
  │  July 14, 2022           │
  │  4.2 MB · 4032×3024      │
  │  /photos/trips/florida/  │
  │  ───────────────         │
  │  Albums: Florida 2022    │
  │  People: Viktor, Anna    │
  │  Tags:   Trip  Summer    │
  │  ───────────────         │
  │  [ Edit ]  [ Open ]      │
  └──────────────────────────┘
```

**ThumbnailCard — hover:**
```
[ photo + gradient overlay ]
[ ☆ ]  [ ✎ ]  [ ⋯ ]      ← quick-action buttons
[ Jul 14, 2022 ]           ← caption
```

**ThumbnailCard — selected:** 2px accent border + checkmark badge top-left + accent fill

**Multi-select mode** (Ctrl+click or checkbox):
- Top bar shifts to: `47 selected  [ Tag ] [ Album ] [ Move ] [ Delete ] [ ✕ ]`
- All standard nav stays accessible — user isn't locked in

### 4. Photo Detail View

Breadcrumb: `Library > Florida 2022 > IMG_4821.heic`
← Back preserves scroll position in previous grid

```
┌────────────────────────────────┬───────────────────────────┐
│                                │  FILE                     │
│                                │  IMG_4821.heic            │
│   [ IMAGE / VIDEO PREVIEW ]    │  HEIC · 4032×3024 · 4.2MB│
│                                │  ─────────────────        │
│  [ ◀ previous ]  [ next ▶ ]   │  CAPTURED   Jul 14, 2022  │
│                                │  MODIFIED   Jul 15, 2022  │
│                                │  ADDED      Jun 20, 2026  │
│                                │  ─────────────────        │
│                                │  LOCATION                 │
│                                │  📍 Florida Keys          │
│                                │  [ Locate in Folder ]     │
│                                │  ─────────────────        │
│                                │  ALBUMS                   │
│                                │  Florida 2022             │
│                                │  [ + Add to album ]       │
│                                │  ─────────────────        │
│                                │  PEOPLE                   │
│                                │  Viktor   Anna            │
│                                │  [ + Tag person ]         │
│                                │  ─────────────────        │
│                                │  TAGS                     │
│                                │  Trip  Summer  Ocean      │
│                                │  [ + Add tag ]            │
│                                │  ─────────────────        │
│                                │  NOTES                    │
│                                │  Click to add a note...   │
│                                │  ─────────────────        │
│                                │  [ Save Changes ]         │
└────────────────────────────────┴───────────────────────────┘
```

- All fields editable inline — no separate edit mode to enter
- Unsaved changes: yellow dot on breadcrumb + "Save Changes" button becomes active
- Save → SQLite update → toast "Saved" with undo link
- Compact: metadata slides up as bottom sheet when "Edit" tapped; preview stays full-width

**Video playback (inline):**
- Videos play directly in the preview area via LibVLCSharp `VideoView` embedded in WinUI 3
- Controls: play/pause (spacebar), seek bar, volume, fullscreen, mute
- Supported containers via bundled libvlc: `.mov`, `.mp4`, `.m4v`, and any container libvlc handles
- Thumbnail shown while video loads (generated by FFmpeg at index time)
- If libvlc fails on a specific file: fallback to Windows Media Foundation `MediaPlayerElement`; if both fail: "This video format couldn't be played. [ Open with system player ]"

### 5. Filter View

```
TOP BAR: [ PhotoAlbum > Filter ]  [ Clear all ]  [ 143 results ]

FILTER BUILDER:
  Match  [ All ▾ ]  of the following conditions

  [ People ▾ ]  [ is ▾ ]       [ Viktor ▾ ]     [✕]
  [ Tag ▾ ]     [ is ▾ ]       [ Trip ▾ ]       [✕]
  [ Date ▾ ]    [ after ▾ ]    [ Jul 2022 ▾ ]   [✕]
  [ + Add condition ]

RESULTS:
  (same responsive ThumbnailCard grid)

EMPTY STATE:
  🔍
  No photos match these filters
  Try removing a condition or broadening your date range
  [ Remove last condition ]  [ Clear all filters ]
```

Filter operators: is / is not / contains / before / after / between / has any of / has all of

### 6. Albums

Grid of AlbumCards → click → media grid with album name in breadcrumb

```
AlbumCard:
┌─────────────────────┐
│  [2×2 photo mosaic] │
│  Florida 2022       │
│  47 items           │
└─────────────────────┘
  Hover: [ ✎ Rename ] [ ⋯ ]
```

Empty album: EmptyState — "No photos yet — add some from your library"

### 7. People

Grid of PersonCards → click → all media tagged with that person

```
PersonCard:
┌─────────────┐
│  [avatar /  │
│   best face]│
│  Viktor     │
│  84 photos  │
└─────────────┘
```

**Tagging model: fully manual.**
User types a person's name on any photo; that name becomes a Person record. No face detection or automatic grouping in v1.

**Future / v2 — Face Detection (documented, not in scope):**
Feasible addition using:
- `Windows.Media.FaceAnalysis` (built-in, free) to locate faces in each image during indexing
- ONNX Runtime + ArcFace embedding model (~50MB) to convert each face region to a 128-float identity vector stored in SQLite
- DBSCAN clustering on vectors post-index to group probable same-person faces
- A "Who is this?" confirmation screen where the user names each cluster
This would not require a database redesign — embeddings become a column in `MediaPerson`. Deferred to v2 pending user feedback on manual tagging.

### 8. Places

List with item counts and optional GPS coordinates.
Click → media grid filtered to that place.
Map view: placeholder in MVP ("Map view — coming soon").

### 9. Events

Grid of EventCards → click → media grid for that event

```
EventCard:
┌──────────────────────────────┐
│  [cover photo]               │
│  Viktor's Birthday 2015      │
│  Jun 12, 2015  ·  23 items   │
└──────────────────────────────┘
```

### 10. Bulk Edit Dialog

```
┌─────────────────────────────────────────────┐
│  Edit 47 items                          [✕] │
├─────────────────────────────────────────────┤
│  ADD TAGS                                   │
│  [Viktor ✕] [Trip ✕]   [ + tag... ]         │
│                                             │
│  ADD TO ALBUMS                              │
│  [Florida 2022 ✕]      [ + album... ]       │
│                                             │
│  ADD PEOPLE                                 │
│  [Viktor ✕] [Anna ✕]   [ + person... ]      │
│                                             │
│  ADD EVENT                                  │
│  [ Select or create event... ▾ ]            │
│                                             │
│  MOVE FILES  (optional)                     │
│  [ Choose destination folder... ]           │
│                                             │
├─────────────────────────────────────────────┤
│  SUMMARY OF CHANGES                         │
│  ✓ Add tags: Viktor, Trip                   │
│  ✓ Add to album: Florida 2022               │
│  ✓ Tag people: Viktor, Anna                 │
│  — No files will be moved                   │
│                                             │
│  A rollback point will be saved.            │
│                                             │
│      [ Cancel ]  [ Apply to 47 items ]      │
└─────────────────────────────────────────────┘
```

Apply is disabled until at least one change is staged.
After apply: toast "Applied to 47 items · **Undo**"

### 11. Settings Panel

Right panel (wide) or full-screen (compact). Sections use the same MetadataRow component as the detail panel — consistent look, inline editable values.

- **Library** — root folder path, rescan, re-index, exclusions
- **Appearance** — thumbnail default size, sort default, theme override (System / Light / Dark)
- **Metadata** — ExifTool path, **Write metadata back to original files** (global toggle, off by default; when on, all saves also update the file's EXIF/XMP data via ExifTool), date format preference
- **Local API** — enable toggle, port, active keys, permission presets, audit log link
- **Backup & Clone** — destination, last run status, schedule
- **About** — version, open-source licenses, diagnostics export

### 12. Clone / Backup Progress

Full-screen (major operation — not a sheet):
```
  Cloning to: E:\PhotoArchiveClone\                   [ Cancel ]

  COPYING FILES
  ████████████████░░░░░░░  14,231 / 18,420 files
  912.4 GB / 982.3 GB  ·  847 MB/s
  /photos/trips/florida/IMG_4821.heic

  VERIFYING HASHES
  ██████░░░░░░░░░░░░░░░░░  3,102 verified

  Estimated remaining: ~18 min          [ Pause ]
```

On complete:
```
  ✅  Clone Complete
  18,420 files  ·  982.3 GB  ·  all hashes verified
  Destination: E:\PhotoArchiveClone\
  [ View Report ]  [ Open Destination ]  [ Done ]
```

On partial failure:
```
  ⚠️  Clone completed with errors
  18,401 copied  ·  19 failed  ·  18,401 verified
  [ View Error Log ]  [ Retry Failed Files ]  [ Done ]
```

---

## UX Acceptance Criteria

Measurable pass/fail thresholds. All must be met before shipping.

| Workflow | Criterion | Pass threshold |
|----------|-----------|----------------|
| First launch | Time from app open to indexing started | ≤ 2 clicks, ≤ 15 seconds |
| Grid render | Time to show first visible thumbnails after launch | ≤ 1.5 seconds |
| Grid scroll | Frame rate while scrolling 10,000-item library | ≥ 60 fps |
| Search | Time from keystroke to visible results | ≤ 300ms |
| Filter | Time from "Apply" to filtered grid rendered | ≤ 500ms |
| Bulk tag | Tag 50 photos in one operation | ≤ 4 user actions |
| Detail open | Time from thumbnail click to detail view rendered | ≤ 400ms |
| Save metadata | Time from "Save" to confirmed write | ≤ 200ms |
| Undo | Time from "Undo" to reverted state | ≤ 500ms |
| Discoverability | New user locates Filter view without guidance | ≤ 10 seconds |
| Error recovery | User recovers from a failed rescan without help | Self-evident from UI |
| Accessibility | All core workflows completable by keyboard alone | 100% |

---

## Accessibility Requirements

Built in from the start — not added after.

- **Keyboard navigation:** full Tab order, arrow keys in grids/lists, Enter/Space to activate, Escape to dismiss/back
- **Focus rings:** 2px accent-color outline on every focused element, never suppressed
- **Screen reader:** all controls have `AutomationProperties.Name`; dynamic content announces via `LiveRegionChanged`; images have descriptive labels
- **Contrast:** WCAG AA minimum (4.5:1 body text, 3:1 large text and UI components); verified in Light, Dark, and High Contrast
- **Text scaling:** all type uses design tokens that inherit system text scale factor (up to 200%)
- **High Contrast mode:** `AccessibilitySettings.HighContrast` respected; no information conveyed by color alone
- **Reduced motion:** `UISettings.AnimationsEnabled` respected — transitions become instant when off

---

## Indexing Engine

1. User selects root folder
2. Rust scanner discovers all supported files recursively
3. Rust computes BLAKE3 hash, file size, media type, timestamps per file
4. C# reads EXIF/XMP/QuickTime metadata via ExifTool
5. C# generates thumbnail via Rust thumbnailer
6. C# writes record to SQLite
7. UI updates reactively as records land — user can browse before indexing completes

### Moved / Renamed File Tracking
`MediaItem` keyed by `content_hash`. On rescan, Rust re-hashes; C# relinks by hash. No duplicates for moved files.

Fields: `content_hash`, `file_size`, `original_path`, `current_path`, `file_name`, `capture_timestamp`, `windows_file_id` (optional)

---

## Database Schema (SQLite)

```sql
MediaItem         -- one row per unique file (keyed by content_hash)
MediaFileLocation -- current and historical paths
Album
AlbumMedia        -- many-to-many
Tag               -- id, name, parent_tag_id, type  (nested tag tree)
MediaTag          -- many-to-many
Person
MediaPerson
Place
MediaPlace
Event
MediaEvent
Metadata          -- raw EXIF/XMP key-value per media item
ThumbnailCache
OperationLog      -- every write; supports rollback
UserSettings      -- per-view sort/size prefs, theme override
BinaryManifest    -- expected hashes for ExifTool and FFmpeg binaries
```

### Nested Tag Example
```
Tag(id=1, name="Viktor",       parent=null)
Tag(id=2, name="Birthday",     parent=1)
Tag(id=3, name="2015",         parent=2)
Tag(id=4, name="Trips",        parent=1)
Tag(id=5, name="Florida 2022", parent=4)
```

---

## Metadata Strategy
- All app metadata in SQLite first — original files never modified by default
- Optional "Write metadata to file" via ExifTool — user-initiated, per-item or bulk
- EXIF/XMP/QuickTime read at index time, stored in `Metadata` table

---

## Safe Delete Model
Explicit options on every delete action:
1. Remove from this album only
2. Remove from all albums, keep indexed
3. Remove from app library only (unindex, file untouched)
4. Move to Recycle Bin
5. Permanent delete — requires explicit confirmation; disabled by default in Settings

---

## Local REST API

- **Disabled by default** — user enables in Settings with explicit toggle
- Binds to `127.0.0.1` only; never 0.0.0.0
- Built with ASP.NET Core minimal API (in-process)
- All routes versioned under `/api/v1/`

### Health Endpoint
```
GET /api/v1/health
→ 200 { "status": "ok", "libraryItems": 18420, "indexing": false }
```

### Structured Error Response (all error responses)
```json
{
  "code": "PERMISSION_DENIED",
  "message": "This API key does not have files.delete scope.",
  "requestId": "uuid"
}
```

### Rate Limiting
- 60 requests/second on loopback (prevents runaway scripts from locking SQLite)
- 429 response with `Retry-After` header on breach

### Idempotency
- All write endpoints (`PUT`, `DELETE`) are idempotent — safe to retry
- `POST /api/v1/tags/add` with same tag on same item is a no-op, returns 200

### OpenAPI Spec
ASP.NET Core generates OpenAPI spec automatically (Swashbuckle). Available at:
`GET /api/v1/openapi.json` when API is enabled.

### Permission Scopes
| Preset | Scopes |
|--------|--------|
| Read Only | library.read, media.read, metadata.read, tags.read, albums.read |
| Tagging / Metadata | + metadata.write, tags.write |
| File Organization | + files.move, files.rename, albums.write |
| Full Control | all scopes including files.delete, jobs.run, admin.config |
| Custom | user-defined selection |

### API Key Handling
- Key shown **once** at generation — copy it immediately, app cannot display it again
- Stored: name, prefix, SHA-256 hash, scopes, created, expires, session ID — never raw key
- Expiry enforced by app on every request
- All API writes logged to `OperationLog` and API audit `.md` files

---

## Security Hardening

### 1. Bundled Binary Integrity (ExifTool + FFmpeg)
At startup, `StartupIntegrityService` computes the SHA-256 hash of each bundled binary and compares it against `BinaryManifest` in SQLite (written at install time by the installer).

On mismatch:
- App refuses to spawn the binary
- User sees: "A required tool file has been modified or corrupted. Reinstall the app to continue."
- Event logged to diagnostic log

### 2. SQLite Encryption at Rest
Decision recorded here explicitly:

**MVP:** SQLite unencrypted. Rationale — personal desktop app on user's own machine; Windows user account access controls the file. Accepted risk. Documented.

**Post-MVP option:** Evaluate SQLCipher with a DPAPI-derived key (tied to Windows user account, zero extra password burden). Implement if user feedback or threat model warrants.

### 3. Database Startup Integrity Check
`StartupIntegrityService` runs `PRAGMA integrity_check` on every launch.

On failure:
- User sees: "Your library database may be corrupted. Would you like to restore from a backup or rebuild your index from your original files?"
- Options: Restore from last clone, Re-index from scratch, Exit
- Corrupted DB renamed to `library_corrupted_[timestamp].sqlite` — never deleted

### 4. Subprocess Isolation (ExifTool / FFmpeg)
- Both processes spawned with minimum required privileges
- No shell expansion — arguments passed as array, never as a concatenated string (prevents command injection)
- Process stdout/stderr captured; process kill on timeout (30s default)
- ExifTool and FFmpeg never run if binary integrity check failed

---

## Testing Strategy

### C# — Unit Tests (xUnit)
Every service has a dedicated test project. Tests inject fakes through the `IScanner`, `IHasher`, etc. interfaces — no native DLL required.

| Service | Test focus |
|---------|-----------|
| `IndexOrchestrator` | Scan → hash → metadata → DB write pipeline; rescan/relink logic |
| `CommitChangeService` | Metadata edits, tag add/remove, album membership |
| `UndoRollbackService` | Operation log replay, rollback correctness |
| `TagService` | Nested tag CRUD, parent/child integrity |
| `AlbumService` | Many-to-many membership, album delete behavior |
| `ApiKeyService` | Key generation, hash verification, scope enforcement, expiry |
| `StartupIntegrityService` | Binary hash check, DB integrity check paths |

### C# — Integration Tests (xUnit + real SQLite)
- In-memory or temp-file SQLite database per test run
- Tests cover: index round-trip, filter queries, bulk edit + rollback, clone manifest generation
- No mocking of the database layer — real SQLite writes

### Rust — Unit Tests (`#[cfg(test)]`)
Every crate has in-module unit tests:
- `scanner`: discovers expected files, skips unsupported extensions
- `hasher`: known-hash test vectors (BLAKE3 reference outputs)
- `dedup`: correctly identifies duplicates vs. distinct files
- `copy_engine`: copy + verify round-trip on temp files
- `verifier`: detects hash mismatch on corrupted destination file

### Rust — Benchmarks (criterion)
- `hasher`: throughput against 100MB, 1GB test files
- `scanner`: file/s rate against directories of 1K, 10K, 100K files
- `thumbnailer`: thumbnails/s for JPEG, HEIC, MOV input

### WinUI 3 — UI Automation Tests
- Key user flows automated via `Microsoft.Windows.Apps.Test`
- Covered flows: first-launch → folder select → indexing starts; thumbnail click → detail view opens; bulk select → tag → apply → undo; filter → results appear

### Test Coverage Gate
No milestone ships without:
- All C# service unit tests passing
- All Rust unit tests passing
- Integration tests passing against real SQLite
- UI automation: first-launch and detail-view flows passing

---

## Error Handling & Logging Strategy

### Internal Diagnostic Log (Serilog → rolling file)
Location: `%AppData%\PhotoAlbum\logs\photoalbum_.log` (rolling daily, 7-day retention)

Logged events (structured JSON):
- App startup / shutdown
- Index start/stop/error per file
- FFI call errors (error code + file path)
- ExifTool/FFmpeg process errors
- SQLite write errors
- API request errors (no PII in log)
- Clone start/stop/file errors
- Binary integrity check results
- DB integrity check result

User-facing diagnostic export: Settings → About → "Export Diagnostic Log" (sanitized copy).

### Error Recovery Matrix

| Failure | User sees | Recovery offered |
|---------|-----------|-----------------|
| Folder unreadable at index | InfoBar: "Can't read [folder]. Check permissions." | Open folder / Skip / Retry |
| ExifTool crash on file | File indexed without metadata; warning badge | Retry metadata / Skip |
| Rust FFI returns error | File skipped; logged | Retry / Skip |
| SQLite write locked | Retry 3× automatically; then InfoBar | Retry / Restart |
| SQLite corrupted on startup | Full-screen recovery dialog | Restore / Rebuild / Exit |
| Binary hash mismatch | App refuses to launch affected tool | Reinstall prompt |
| Clone file copy fail | Per-file error in report | Retry failed / Skip |
| API key expired | 401 with structured error body | Re-enable key in Settings |

---

## Clone / Backup

### Function Modes
- Clone Entire Library
- Clone Selected Albums
- Clone Selected Tags / People / Events
- Clone Selected Folder / Subtree
- Clone Current Filter Results

### What Is Cloned
- Original photos/videos (folder structure preserved)
- SQLite database
- Thumbnail cache (optional)
- App executable and config
- API audit records (optional)
- Backup manifest + verification report

### Clone Destination Layout
```
PhotoArchiveClone\
  PhotoAlbum.exe
  app\
  library\
    photos\
    videos\
  data\
    library.sqlite
    thumbnails\
  config\
    portable.json
  API\
  backup\
    clone_manifest.json
    verification_report.md
```

### Clone Manifest
```json
{
  "cloneId": "uuid",
  "sourceLibraryId": "uuid",
  "cloneType": "entire-library",
  "createdAt": "2026-06-20T09:45:00",
  "mediaItems": 18420,
  "bytesCopied": 982331122944,
  "hashVerification": "passed",
  "portableMode": true
}
```

### Safety Requirements
- Never overwrite destination without explicit warning
- Resume if interrupted (manifest tracks completed files)
- Show failed/skipped files in post-clone report
- Keep original file timestamps
- Maintain relative paths
- Verify before declaring success
- Clone optionally read-only

---

## Rust Modules (photoalbum_core)

| Module | Responsibility |
|--------|---------------|
| `ffi` | C-compatible exports only; `catch_unwind` on every entry point |
| `scanner` | Recursive file discovery, extension filtering |
| `hasher` | BLAKE3 content hashing |
| `dedup` | Duplicate detection by hash |
| `thumbnailer` | Thumbnail generation pipeline |
| `copy_engine` | Verified file copy with resume support |
| `verifier` | Post-clone integrity checking |

---

## C# Modules

| Module | Responsibility |
|--------|---------------|
| `AppShell` | Adaptive nav, breakpoint management, theme binding |
| `DesignSystem` | Design tokens, theme resources, shared control styles |
| `StartupIntegrityService` | Binary hash verify, DB integrity check on launch |
| `IndexOrchestrator` | Drives IScanner, IHasher, IThumbnailer; writes SQLite; fires UI events |
| `CommitChangeService` | Applies metadata/tag/album edits; writes OperationLog |
| `UndoRollbackService` | Replays OperationLog in reverse |
| `TagService` | CRUD for nested tag tree |
| `AlbumService` | CRUD for albums and membership |
| `ClonePlannerService` | Scope selection; drives ICopyEngine |
| `ApiKeyService` | Key gen, hash, scope enforcement, expiry |
| `LocalApiServer` | ASP.NET Core minimal API `/api/v1/`; health + OpenAPI |
| `ExifToolWrapper` | Spawns ExifTool safely; parses output |
| `ThumbnailManager` | Cache management; calls IThumbnailer |
| `VideoPlayerService` | LibVLCSharp player lifecycle; WMF fallback; playback state |
| `DiagnosticLogger` | Serilog rolling file; sanitized export |
| `UserSettingsService` | Per-view sort/size prefs, theme override, write-back global toggle |

---

## MVP Build Order

1. Rust workspace scaffold: `ffi`, `scanner`, `hasher` with unit tests and criterion benchmarks
2. C# shell: WinUI 3, DesignSystem tokens, adaptive AppShell, DI container, SQLite setup
3. `StartupIntegrityService`: binary hash check, DB integrity check, HEIC/video readiness banners
4. Indexing pipeline: `IScanner` → ExifTool metadata → SQLite → reactive UI updates
5. `IThumbnailer` + FFmpeg video thumbnails + responsive auto-column grid (virtualized)
6. Photo detail view + adaptive metadata panel; inline edit + save
7. Inline video playback: LibVLCSharp `VideoView` + WMF fallback + `VideoPlayerService`
8. Tag / album CRUD; nested tag tree
9. Filter view + filter builder
10. People / places / events (manual tagging)
11. Multi-select + bulk edit dialog + `UndoRollbackService`
12. All empty / loading / error states on every view
13. Safe delete modes
14. Accessibility pass: keyboard nav, focus rings, screen reader labels, contrast audit
15. UX acceptance criteria validation against measurable thresholds
16. Local API: `/api/v1/`, health, OpenAPI, rate limit, structured errors
17. Clone/backup: Rust `copy_engine` + `verifier` + full progress view
18. Installer (MSIX/WiX, self-contained, binary manifest written at install time)
19. GitHub push: public repo, clean history, `.gitignore`, license file

---

## Prerequisite Handling

### Development
- Portable `.exe`; local `appsettings.json`; local SQLite; local thumbnail cache; ExifTool + FFmpeg in `tools\`

### Production Installer
- Self-contained .NET (no separate runtime install required)
- `photoalbum_core.dll` (Rust) bundled
- ExifTool + FFmpeg bundled and hash-verified
- LibVLCSharp + libvlc native libraries bundled (covers MOV/MP4/M4V without user installing VLC)
- `BinaryManifest` written to SQLite at install time (SHA-256 of all bundled binaries)
- On first launch: HEIC/HEVC codec check → non-blocking banner with Microsoft Store deep link if absent
- On first launch: LibVLCSharp load test → non-blocking warning if fails

### GitHub Release Strategy
- Repository: **public**, created at project completion — not during development
- During development: code lives only in this local working directory
- At completion: full repo pushed in one clean initial commit with proper `.gitignore` (excludes `data/`, `logs/`, `thumbnails/`, `tools/` binaries, `config/portable.json`)
- Bundled binaries (ExifTool, FFmpeg, libvlc): not committed to Git — installer downloads or packages them separately
- License: to be decided before public push (MIT or Apache 2.0 recommended for a personal tool)

---

## Complexity Assessment

**Overall: Medium-High**

| Area | Risk | Mitigation |
|------|------|------------|
| Rust/C# FFI boundary | Medium | Thin contract, `catch_unwind`, interface abstractions |
| HEIC/HEIF on Windows | Medium | Codec check at first run with prompt |
| iPhone .MOV GPS/metadata | Medium | ExifTool handles most; documented gaps |
| Moved file tracking | Medium | Hash-based relink is proven |
| Adaptive layout (3 breakpoints) | Medium | WinUI 3 `VisualStateManager` |
| Reactive grid during indexing | Medium | Incremental SQLite + UI events |
| Rust Cargo workspace | Low | Standard pattern; criterion benchmarks from day one |
| Testing coverage | Medium | xUnit + Rust tests + UI automation — built in, not bolted on |
| SQLite startup integrity | Low | `PRAGMA integrity_check` at launch |
| Binary hash verification | Low | SHA-256 at startup against installer-written manifest |
| Nested tags + many-to-many | Low | Straightforward SQL |
| Thumbnail scale | Low | Rust pipeline |
| Clone integrity | Low | Rust verifier |
| Safe delete safety | Low | Recycle Bin first, explicit confirm |
| Accessibility compliance | Medium | Built in from step 1, audited at step 13 |
| WinUI 3 learning curve | Medium | More modern than WPF; strong MS docs |
