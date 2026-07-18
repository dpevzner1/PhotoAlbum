# Lessons Learned — iPhone Connect & Backup Feature

A formal retrospective of the iPhone photo/video backup feature: every issue
hit, what was tested, what worked, what led to the next phase, and what brought
it to a working production state. Purpose: **prevent duplicate work, cut
time-to-completion on similar features, and raise the next project's
efficiency.** Read this before building any WPD/MTP, external-device, or
large-media-set feature.

Status: **Working end-to-end** — 60,185 items / 261.8 GB enumerated with zero
faulted folders; live grid + video preview flawless; journaled resumable
backup ready.

---

## 1. Executive summary — the five root causes

Nearly all lost time traced to **five** distinct root causes, none of which
were in our application logic at first, and each of which masqueraded as the
others:

| # | Root cause | Symptom it wore | Real fix |
|---|---|---|---|
| A | Apple USB driver absent (Windows Update driver-search disabled by policy) | "0 photos", "requires further installation" | winget install from Apple's CDN |
| B | iOS trust/session gate | storage `(none)` even unlocked | Reset Location & Privacy → re-Trust |
| C | Windows services stopped (WPDBusEnum, Apple Mobile Device Service) | phone vanished from Explorer AND app | in-app **Repair Connection** (restart services + re-register device) |
| D | PTP session souring from connect/disconnect churn | intermittent `0x8007001E` read faults | **persistent single session** (mirror Explorer) |
| E | UI rendering 60k tiles in a non-virtualizing WrapPanel | app freeze *after* a successful scan | **cap rendered grid**, keep full set in memory for backup |

**Meta-lesson:** the failure was almost never where the symptom pointed. Layered
diagnosis (hardware → driver → service → OS session → iOS session → our code →
our UI) beat guessing every time.

---

## 2. Timeline: issue → test → outcome → next phase

### Phase 1 — Build (assumed happy path)
- Built detection (WM_DEVICECHANGE), MTP enumeration (MediaDevices), journaled
  backup, video telemetry, VLC playback. All compiled and smoke-tested.
- **Outcome:** worked in code; **nothing** worked against the real phone yet.
- **Lesson:** a green build and a startup smoke test do **not** validate a
  hardware-integration feature. Real-device testing is a separate, mandatory
  gate — and it surfaced *five* issues the build could never catch.

### Phase 2 — "Detected but 0 photos"
- Tested: Device Manager (device present), Explorer (folder empty), our logs
  (`Storages: (none)`).
- Found: **driver missing** + **trust not granted**.
- Fixed: winget driver bootstrap; unlock + Trust; for a stuck prompt, **Reset
  Location & Privacy**.
- **Outcome:** first fully-working session — 15,525 items enumerated.
- **Next phase:** it worked *once*, then later sessions failed → session-decay
  investigation.

### Phase 3 — iOS 17+ layout
- Tested: Explorer showed date folders (`202105_h`), **no DCIM**.
- Fixed: scan DCIM when present, else the storage root with a media-extension
  whitelist.
- **Lesson:** never hardcode `DCIM`. iOS changed the on-device layout.

### Phase 4 — Intermittent read faults + freezes
- Tested progressively: retry-with-backoff (didn't help — not a warm-up issue);
  folder-by-folder walk (helped — isolated faults, salvaged 38k items on a
  soured session); progress-aware watchdog (replaced a wall-clock timeout that
  was killing healthy multi-minute scans).
- Consulted **APlusTech agent** for Windows Event Viewer analysis (DCOM 10016,
  UMDF log state).
- Found the pattern: **our per-operation Connect/Disconnect + 20s watcher poll
  = thousands of session cycles/day, which iOS eventually punishes with
  permanent read faults for our client — while Explorer's ONE long-lived
  session kept working.**
- Fixed: **persistent session** (connect once, reuse, drop only on
  removal/fault); auto-renegotiate a session that was opened pre-unlock.
- **Outcome:** the decisive fix — **60,185 items, 0 faulted folders.**

### Phase 5 — Freeze after a *successful* scan
- Tested: log showed scan complete at 60k items, then 6 min of UI silence.
- Found: 60k Adds to a bound collection + WPF **WrapPanel doesn't virtualize**
  → all tiles realized → UI-thread death.
- Fixed: cap the rendered grid (500), single bulk populate, keep the full set
  in memory for backup/counters/select-all.
- **Outcome:** grid + previews flawless → **production-ready.**

---

## 3. What worked vs. what didn't (so you don't retry the dead ends)

**Worked:**
- Layered, evidence-first diagnosis; separating raw observation from hypothesis.
- **Persistent single session** — the single highest-impact fix.
- **Folder-by-folder walk** with per-folder fault isolation and partial results.
- **Progress-aware watchdog** (heartbeat) instead of wall-clock timeouts.
- In-app **Repair Connection** (WPDBusEnum + Apple service restart + device
  re-register) — reproduces the manual fix as one elevated click.
- Driver **bootstrap from Apple's CDN via winget** — legal (no redistribution)
  and self-healing.
- **Run log as the source of truth** — every fix was confirmed by a log
  signature, never by assumption.

**Did NOT work / dead ends (don't repeat):**
- Retry-with-backoff alone for read faults — the fault wasn't warm-up, it was
  session souring; backoff just delayed the same failure.
- Wall-clock scan timeout — killed healthy multi-minute scans; must be
  progress-based.
- Rebooting the *phone* — ruled out a wedged iOS daemon; not the cause.
- Restarting the *PC* alone — fixed services once but didn't address session
  churn.
- Opening **Explorer to "verify visibility"** while the app scanned — actively
  harmful: PTP is effectively single-client, so Explorer starved our app.
- Naive per-item `ObservableCollection.Add` at scale + non-virtualizing panel.

---

## 4. Reusable engineering principles (carry to the next project)

1. **Hardware/OS integration needs a real-device test gate**, separate from
   build success. Budget for a diagnosis phase; the first working session is
   the start, not the finish.
2. **Diagnose in layers, bottom-up:** hardware bus → driver → OS service →
   OS session → vendor(iOS) session → app logic → app UI. Log a one-line health
   summary at each layer (we log `Driver/AppleService/WPDBusEnum`).
3. **Instrument before theorizing.** Every hypothesis needs a falsifiable log
   signature. The run log (overwritten per launch, human-readable) was worth
   more than any single fix.
4. **External vendor sessions can sour under churn.** For MTP/PTP/WPD and
   similar, hold **one persistent session** and mirror the platform's own tool
   (Explorer) rather than open/close per operation.
5. **Timeouts must be progress-aware, not wall-clock**, for anything that can
   legitimately run long (big transfers, big scans).
6. **UI must virtualize or cap for large sets.** WPF `WrapPanel` does NOT
   virtualize — use a virtualizing panel or render a bounded slice and keep the
   full data model separate. Bulk-populate bound collections (one reset, not N
   Adds).
7. **Own the full dependency lifecycle** when a required driver can't be
   bundled (license): detect → install-on-demand → verify → repair, all
   in-app.
8. **Honest limits beat hidden ones:** MTP has no change journal (full rescan
   each time) and no device-side hash; iCloud-optimized libraries exceed device
   capacity. Document these; don't paper over them.
9. **Startup system check + one-click repair** turns a multi-hour manual
   diagnosis into a user-serviceable action. Build the repair path into the app
   once you've found the manual fix.

---

## 5. Operational runbook (the recipe that works)

For this phone on this machine, the reliable path:
1. Ensure Apple driver + services healthy (startup check logs it; **Repair
   Connection** fixes it).
2. iPhone **unlocked, on the home screen** (close Photos; don't open Explorer
   to the phone).
3. Plug in **while unlocked**. If storage stays `(none)` across a Refresh:
   **Settings → General → Transfer or Reset → Reset → Reset Location &
   Privacy**, then replug + **Trust**.
4. Refresh → scan runs on the persistent session (minutes for ~60k items).
5. Attach an external drive with headroom (library can exceed the PC disk),
   **Browse…** to it, **Type: All**, **Back Up All** (journaled, resumable).

---

## 6. Cross-references

- [phone-connectivity-remediation.md](phone-connectivity-remediation.md) — issue-by-issue log with exact log signatures
- [phone-backup-resilience.md](phone-backup-resilience.md) — journaled/resumable backup design
- [phone-feature-validation.md](phone-feature-validation.md) — industry comparison + DevOps grade
- [iphone-backup-feasibility.md](iphone-backup-feasibility.md) — original research & scope
- [apple-driver-install.md](apple-driver-install.md) — driver bootstrap commands
- [video-support.md](video-support.md) — VLC playback + telemetry

## 7. Open items carried forward
- **Free-space preflight** before backup (refuse if destination < needed).
- **Cached inventory** for instant reconnect view + background rescan (queued;
  gated on backup being fully tested end-to-end).
- Consider a **virtualizing wrap panel** so the grid can show the full set
  instead of a 500-item slice.
