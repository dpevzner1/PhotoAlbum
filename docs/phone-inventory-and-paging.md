# Phone Inventory Cache, Paging & Memory Efficiency

## Why not GPU acceleration
Rendering a thumbnail grid is **not compute-bound** — WPF already GPU-composites
via DirectX. The real costs are (a) RAM from realizing UI containers and (b) MTP
thumbnail I/O over USB2. A GPU addresses neither. The correct levers are
**bounded rendering + paging + a disk cache** (DevOps engine: bounded buffers,
lazy loading, backpressure).

## Paging (PhoneViewModel)
- Page size 500; **exactly one page (<=500 tiles) is ever realized** → flat RAM
  regardless of library size (tested at 60,185 items).
- Pager UI: First / Prev / Next / Last with `from–to of total (page x/y)`.
- Thumbnails load **for the current page only**, cancelled on navigation — at
  most one page of MTP fetches in flight.

## Persistent inventory cache (PhoneInventoryService)
`%LOCALAPPDATA%\PhotoAlbum\phone-cache\<device>\`:
- `inventory.json` — last scan's item list (id, name, folder, size, date,
  isVideo). **Shown instantly on reconnect** while a fresh scan runs.
- `thumbs\<hash>.jpg` — thumbnail bytes; revisits/next-connect load from disk,
  no MTP round-trip.

**Incremental sync:** the fresh scan is authoritative; the VM computes and shows
**"N new since last sync"**. New media is simply added; nothing re-downloads.

## Backup checklist (what was / wasn't backed up, and where)
- **Backed up:** per-device BLAKE3 hash index (`phone.backup_index.<key>`) +
  the journal written into each destination folder (per file: hash, final name,
  destination path). The operation log records `dest = <full path>` per file.
- **To which folder:** the journal lives inside the destination; op-log has the
  absolute path.
- **Not yet backed up:** any device item whose content hash is absent from the
  index — exactly what "Back Up All (skips duplicates)" transfers next.
