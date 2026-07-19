# Backup Validation, Integrity, Iterative Sync & Acceleration

Answers four questions about the phone backup: post-run validation, failure
handling, overwrite behavior on re-runs, and whether the GPU should be used.

## 1. Validating the full payload after a backup

**What exists today (persistent, at the destination):**
- **`.photoalbum-backup-journal.json`** — written to the backup folder, updated
  after every file. Per item it records: device path (key), **status**
  (`copied`/`failed`), **BLAKE3 hash** (`H`), and **final file name** (`N`).
  Header carries `Total`, `StartedUtc`, `UpdatedUtc`, `DeviceKey`. Example:
  ```json
  "\\Internal Storage\\202108_l\\IMG_0996.MOV":
      {"S":"copied","H":"0950070390a6…","N":"IMG_0996.MOV"}
  ```
- **Operation log** (SQLite) — one row per copied file with hash + absolute
  destination path.
- **Run log** (`COMPILED/log/albumlog.md`) — per-item events and a final
  `Backup finished — copied X, skipped Y, failed Z, N MB` summary.

**How to validate:** the journal is the authoritative manifest — every file's
name and hash is there, and a re-run re-hashes on download and skips only exact
hash matches, so the journal is self-verifying against the phone.

**Queued enhancement (next build):** a human-readable **`backup-report.csv`** at
the destination on completion — `FileName, DeviceFolder, SizeBytes, BLAKE3,
Status, DestPath` + a summary line — plus storing **SizeBytes** in the journal
and preserving the original name on failed entries (today a failed item's `N`
holds the error text). This makes name+size+hash validation a one-file open.

## 2. Failure / error handling during transfer

Already implemented and persistent:
- **Per-item retry** ×4 with backoff (2/5/10 s) for transient MTP faults.
- **Size verification** — received bytes must equal the device-reported size;
  a short/0-byte read (e.g. `0x8007001E` on an iCloud-only original) is treated
  as failure, NOT saved as a corrupt file.
- **Atomic commit** — download to `<name>.part`, verify, rename; a crash leaves
  only `.part` files (swept on the next run).
- **Reconnect-wait** — device lost mid-run pauses up to 60 s and resumes.
- **Failed items are logged in the journal** (re-tryable) and the backup
  continues — one bad file never stops the run.
- **The journal IS the persistent process log with hashes at the destination.**
  Yes, it should exist, and it does.

## 3. Iterative backup / overwriting existing content

**Current behavior — non-destructive by design, never overwrites:**
- **Duplicate = identical content** (BLAKE3 over the whole file). Identical
  content already in a prior backup is **skipped** (no re-copy).
- **Same file name, different content** → saved alongside with an incremented
  suffix (`IMG_0001.HEIC`, `IMG_0001 (1).HEIC`). Existing files are never
  modified or deleted.
- So an iterative/second backup only adds **genuinely new** content; re-running
  after an interruption resumes from the journal.

**Options (queued as a Settings toggle):** offer a per-run mode —
(a) **Skip duplicates** (current safe default), (b) **Keep both** (always
incremented), (c) **Overwrite existing**. Default stays (a); overwrite is
opt-in with a confirmation, since it is the only destructive choice.

## 4. GPU in indexing / copy / transfer — engine review

**DevOps engine principle: match the accelerator to the workload.** Applied
here:

| Stage | Bound by | GPU benefit |
|---|---|---|
| Indexing (MTP folder listings) | USB/MTP I/O latency | **None** — I/O, not compute |
| File download/copy | USB 2.0 read + disk write (~tens of MB/s) | **None** — I/O-bound |
| Integrity hashing (BLAKE3) | Already SIMD on CPU (Rust core), far faster than USB delivers data | **None** — CPU already outruns the I/O; GPU would idle waiting |

**Conclusion: 0% of indexing/copy/transfer should use the GPU.** These are
I/O-bound (or already CPU-SIMD-saturated) workloads; a GPU adds complexity with
no throughput gain because the bottleneck is the USB bus, not computation. GPU
acceleration only pays off for massively-parallel compute — none of which
occurs in the backup path. (Where the app *does* lean on the GPU is display
compositing, which WPF handles automatically — see resource-efficiency-grade.md.)
