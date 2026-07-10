# Video Backup, Inventory & Playback

How video is handled across the app — designed against what already existed
(videos were always first-class `MediaItem`s) plus the gaps closed in 2026-07.

## Inventory & filters

- **Library view**: the existing `Type: All / Photos / Videos` filter applies,
  and every other filter (tags, albums, people, places, events, years,
  favorites, rating) works identically for videos — they share the MediaItem
  model and junction tables.
- **Phone view**: an iPhone-style `Type: All / Photos / Videos` filter with
  live counts (`N photos · M videos · X GB`). Backup selection respects the
  filter view; the duplicate-skipping journaled backup pipeline is shared.

## Playback (play/pause) — bundled codecs

- **Engine:** the bundled **libvlc** (`libvlc\win-x64` ships with the app;
  `LibVLCSharp.WPF` hosts it). VLC decodes H.264/HEVC/ProRes internally —
  **no Windows codec packs are required for playback**.
- **DetailView**: videos play in-app with play/pause, seek, mute, elapsed/total
  time, and an "open in system player" fallback. Transport bar sits below the
  video surface (WPF/WinForms airspace constraint).
- **Phone view**: each video tile has a ▶ preview button — MTP cannot stream,
  so the app downloads a temp copy (size-confirmed over 200 MB), plays it in a
  preview window, and deletes the temp file on close.
- **Licensing note:** Microsoft's HEVC Store codec (used by *Windows* for
  HEVC thumbnails) cannot be redistributed — same posture as the Apple driver.
  VLC removes the need for it in playback; thumbnail HEIC guidance stays the
  Store banner.

## Telemetry / metadata (time, place, region)

`MediaMetadataService` (built on MetadataExtractor, MIT) extracts on import:

| Field | Photos | Videos (iPhone .MOV / .MP4) |
|---|---|---|
| Capture time | EXIF `DateTimeOriginal` | QuickTime movie header `Created` |
| GPS (region/place) | EXIF GPS IFD | Apple `com.apple.quicktime.location.ISO6709` atom (e.g. `+40.7128-074.0060+011.0/`) |
| Duration | — | QuickTime duration / timescale |

- Captured into `MediaItem.CaptureUtc`, `Latitude/Longitude`,
  `DurationSeconds` → flows into the Places view, year filters, and the
  detail overlay exactly like photos.
- Phone backups also **preserve capture time as the file's LastWriteTime** so
  Explorer and later imports show the real date.
- Failure policy: metadata extraction never fails an import (degrades to
  nulls; photo GPS additionally falls back to the legacy WPF EXIF reader).

## Sources

- LibVLCSharp getting started / WPF: https://github.com/videolan/libvlcsharp/blob/3.x/docs/getting_started.md
- MetadataExtractor (.NET): https://github.com/drewnoakes/metadata-extractor-dotnet
- iOS/Android video geolocation metadata (ISO 6709): https://blog.addpipe.com/geolocation-metadata-ios-android-video-files/
