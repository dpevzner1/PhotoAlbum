# Phone Backup — Resilience & Interrupt Protection

Design for surviving connection jitter, device sleep/re-lock, app crash, and
power loss during an iPhone backup, with exact progress accounting and no
corruption or silent data loss. Validated against the DevOps engine's standing
directives: **durable state + rollback path, idempotency, strict error
handling, honest validation limits.**

## Threat model

| Interruption | Consequence without protection |
|---|---|
| USB jitter / cable bump | One file fails mid-stream; naive code aborts the whole batch |
| Device re-locks / sleeps | Device drops off WPD; remaining items all fail |
| App crash / power loss | Progress lost; partial file left looking like a real photo |
| Re-run after any of the above | Everything re-copied; duplicates like `IMG_0001 (1).HEIC` |

## Design — journaled, idempotent, at-least-once with content-addressed dedup

### 1. Durable backup journal (progress that survives anything)
A journal file (`.photoalbum-backup-journal.json`) lives **in the destination
folder** and is rewritten after **every file**:

```json
{
  "version": 1,
  "deviceKey": "<serial>",
  "startedUtc": "...", "updatedUtc": "...",
  "total": 512,
  "items": { "<device path>": { "s": "copied", "h": "<blake3>", "n": "IMG_0001.HEIC" } }
}
```

At any instant — including after a crash — the journal answers: **how many
files total, how many copied, how many failed, how many remain.** Progress
reported to the UI/API carries the same counters
(`Done / Total / Copied / Skipped / Failed`).

### 2. Atomic per-file commit (no torn files, ever)
```
download → <name>.part → size check vs device-reported bytes → BLAKE3 → rename to final
```
`File.Move` on the same volume is atomic: a file either exists complete and
hashed, or it is a `.part` that resume deletes on startup. A final-named file
is never partial.

### 3. Idempotent resume (re-run = continue, not redo)
- Journal items already `copied` are skipped instantly (no re-download).
- The per-device **BLAKE3 hash index** (persisted incrementally, every file)
  catches content copied in *any* earlier session — even to another folder —
  so at-least-once delivery never becomes duplicate files.

### 4. Jitter and disconnect handling
- **Per-item retry**: 3 attempts with backoff (2 s → 5 s → 10 s) for transient
  MTP/COM errors.
- **Reconnect wait**: when the device disappears mid-run, the backup pauses
  and polls for up to 60 s. Devices get a **new WPD DeviceId** on
  re-enumeration, so the device is re-resolved by its stable key
  (serial number) and the run continues where it stopped.
- If the device does not return, the run ends cleanly: journal current,
  partials deleted, remaining count reported — plug back in and press Back Up
  again to resume.

### 5. Corruption prevention — and its honest limit
- Size is verified against the device-reported byte count; the BLAKE3 hash of
  every delivered file is recorded in the journal, the per-device index, and
  the operation log.
- **Limit:** MTP cannot provide a device-side hash, so there is no
  cryptographic proof the phone's original equals our copy; the achievable
  envelope is *complete stream + size match + atomic commit + recorded hash*.
  This is the same envelope commercial iPhone tools operate in, and it is
  documented rather than implied away (DevOps directive: honest validation).

## Failure walkthroughs

| Scenario | Behaviour |
|---|---|
| Cable bump, device back in 5 s | Item retried after backoff; run continues; nothing lost |
| Phone re-locks at file 300/512 | Reconnect wait 60 s → user unlocks → device re-resolved → file 300 retried, run finishes |
| Power loss at file 300 | On next run: `.part` deleted, journal shows 299 copied, run resumes at file 300 |
| Same photos, new destination | Hash index marks them skipped — no duplicate bytes written |

## Industry parity

The iCloud-optimized handling and resilience posture match (and extend) what
iMazing and CopyTrans publicly document for the same problem space — see
[phone-feature-validation.md](phone-feature-validation.md) for the comparison
table, the DevOps engine grade, and sources.

## Free-space preflight (added 2026-07-18)
Before writing any file, `BackupAsync` estimates the bytes still to copy (items
not already in this destination's journal) and refuses if the destination drive
can't hold them (+3% headroom): *"Not enough free space: this backup needs about
X GB but the destination has Y GB free."* Prevents filling a disk mid-run.
Applies to both the UI and the `POST /api/v1/phone/backup` path.
