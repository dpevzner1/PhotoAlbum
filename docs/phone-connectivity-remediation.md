# iPhone Connectivity — Remediation Log

Record of the issues hit while bringing up the Phone connect/backup feature on
a real Windows 10 machine + iPhone, and the fixes applied. Kept as the
troubleshooting playbook for future connectivity reports.

## Timeline of issues and fixes

### 1. Phone menu appeared for USB sticks (false positive)
- **Symptom:** any flash drive made the Phone nav item appear.
- **Cause:** `MediaDevice.GetDevices()` returns *all* WPD devices, including
  mass-storage (MSC) drives.
- **Fix:** `MtpDeviceService` filters to devices whose WPD `Protocol` is
  MTP/PTP only.

### 2. App crashed at startup after adding the feature
- **Symptom:** no window; `FileNotFoundException: MediaDevices, Version=1.10.0.0`.
- **Cause:** the fast-sync build step (`SyncHarness` in the csproj) copies a
  fixed list of assemblies to `COMPILED\` — new NuGet DLLs weren't on the list.
- **Fix:** `MediaDevices.dll` added to `_HarnessSync`.
- **Lesson:** when adding a NuGet package, add its DLL to `SyncHarness`.

### 3. iPhone detected but 0 photos; Device Manager: "requires further installation"
- **Symptom:** nav item appears ("Apple iPhone"), enumeration returns nothing;
  Explorer shows the device but the folder is empty.
- **Cause A — driver:** only Windows' generic MTP driver was bound; the Apple
  Mobile Device USB driver was absent (no iTunes/Apple Devices installed, and
  the machine has policy blocking driver search via Windows Update).
- **Fix A:** installed the driver from Apple's own servers (license-compliant):
  `winget install --id Apple.AppleMobileDeviceSupport --exact --silent --accept-source-agreements --accept-package-agreements`
  Result: Apple INFs (`applekis.inf`, `applersm.inf`) in the driver store, all
  three iPhone USB interfaces Started, no problem codes.
  Full command reference: [apple-driver-install.md](apple-driver-install.md).
- **Cause B — trust:** even with the driver healthy, the phone reported
  **zero storages** (log: `Storages on 'Apple iPhone': (none)`), to our app
  *and* Explorer alike. iOS withholds storage until the phone is unlocked and
  the PC is trusted.
- **Fix B:** unlock phone → replug → tap **Trust This Computer** + passcode.
  (If the prompt never appears: iPhone Settings → General → Transfer or Reset →
  Reset → **Reset Location & Privacy**, then replug while unlocked.)
- **Note:** the photo interface binding to Microsoft's `wpdmtp.inf` is
  **normal** — Apple provides no WPD driver; photo browsing always uses the
  generic MTP driver. The Apple driver pair handles the other USB interfaces.
- **Note:** `Apple Mobile Device Service` may remain STOPPED until a reboot;
  photo browsing works without it. The app's Phone view offers an elevated
  "Start Service" action.

### 4. Trust granted, storage visible — still 0 photos (iOS 17+ layout)
- **Symptom:** Explorer shows `Internal Storage` full of date-named folders
  (`201207__`, `202105_h`, …) — **no DCIM folder at all**.
- **Cause:** newer iOS exposes date-bucketed folders directly under the
  storage root; the enumerator only looked for `DCIM`.
- **Fix:** `FindMediaRoots` uses `DCIM` when present (classic layout),
  otherwise scans the storage root with a media-extension whitelist
  (`.heic .jpg .jpeg .png .dng .mov .mp4 …`).
- **Log signature:** `'\Internal Storage': no DCIM folder — scanning storage root (iOS 17+ date-folder layout)`.

## Diagnostics now built into the app

`PhoneDiagnosticsService` runs on every phone connect / Refresh:

| Check | Failure surface |
|---|---|
| Apple driver present (`usbaapl64.sys` or service registration) | Banner: "Install Driver (from Apple)" → winget bootstrap; Store fallback |
| Apple Mobile Device Service installed & running | Banner: "Start Service" (elevated `sc start`, UAC) |
| Storage/DCIM visibility | Logged (`Storages: …`, layout detection); status line explains unlock/Trust |

One-line health summary is written to the run log per connect:
`PhoneDiag | Driver=OK  Service=STOPPED`.

## Verification signatures (run log, COMPILED\log\albumlog.md)

```
DeviceWatcher | Device set changed — 1 device(s): Apple iPhone     ← detection OK
MtpDevice     | Storages on 'Apple iPhone': \Internal Storage      ← trust OK
MtpDevice     | '\Internal Storage': no DCIM folder — scanning …   ← layout handled
PhoneDiag     | Driver=OK  Service=RUNNING                          ← Windows side healthy
```

### 5. Scan total exceeds the phone's physical storage (e.g. 250+ GB on a 128 GB phone)
- **Symptom:** the scanning counter's byte total is larger than the device's
  storage capacity.
- **Cause (not a false positive):** with iCloud Photos **"Optimize iPhone
  Storage"**, the phone stores small local proxies but advertises every asset
  at its **full original size** over MTP/PTP — and streams the original from
  iCloud when transferred. The scan therefore reports the **logical library
  size**, not local disk usage. Contributors: Live Photo `.MOV` companions
  (real, per-photo) and occasional `.HEIC`+`.JPG` converted pairs.
- **App behaviour:** status line now says "library size" and, when the total
  exceeds capacity, explains that iCloud-optimized originals are included.
  Enumeration dedups by device object id, and the run log prints a post-scan
  breakdown: `Scan complete: N items, X GB advertised. Duplicate ids skipped: …
  Same-name photo pairs (HEIC+JPG?): …  By type: .heic×A=BGB .mov×C=DGB …`
- **Backup implication:** backing up iCloud-optimized items makes the phone
  download originals from iCloud mid-transfer — slow, and failures surface as
  per-item retries. For a full local backup, consider setting Photos →
  **Download and Keep Originals** on the phone first.

### 6. Unlocked + trusted + screen on — storage still (none) [RESOLVED 2026-07-18]
- **Symptom:** phone unlocked with screen on, all Windows layers healthy
  (3 USB interfaces Started, WPDBusEnum + Apple service RUNNING, app detects
  device), yet every scan logs `Storages: (none)`. Also observed: a spontaneous
  21-second USB drop/reconnect with nobody touching the cable.
- **Leading theory — connection-order gate:** iOS decides photo exposure when
  the USB session is ESTABLISHED. Plugged-while-locked → storage stays hidden
  for the whole session even after unlocking. Remedy: unlock first (home
  screen), then plug in.
- **Second suspect — physical:** spontaneous drops suggest a flaky
  cable/port. Next test: different USB port (direct, no hub) + original Apple
  cable.
- **Verified working reference:** 2026-07-10 after Reset Location & Privacy +
  Trust, a scan enumerated 15,525 items / 69.3 GB before being interrupted —
  the full pipeline works when the session is granted.
- **Note:** greyed-out Auto-Lock is caused by Low Power Mode (Settings →
  Battery) — not applicable in this instance (LPM was off).

- **RESOLUTION:** the in-app **Repair Connection** action fixed it — restart
  WPDBusEnum + Apple Mobile Device Service, then restart the iPhone USB device
  node (simulated replug), as one elevated step. This mirrors the original
  bring-up procedure and is now a permanent button in the Phone view.

## Permanent safeguards (2026-07-18)

- **Startup system check:** every app launch runs the full diagnostics
  (Apple driver present, Apple Mobile Device Service, WPDBusEnum) in the
  background and logs one line: `PhoneDiag | Driver=OK  AppleService=RUNNING
  WPDBusEnum=RUNNING`. Any failure logs a warning pointing at the Phone view.
- **Repair Connection button** (Phone view header): one elevated click →
  restart both services + re-register the iPhone device → automatic rescan.
  The service-warning banner also routes here.
- **Driver "packaging":** Apple's SLA forbids shipping the driver binary in
  our installer, so what is packaged is the **bootstrap process**:
  `AppleDriverService` detects the driver and installs it on demand straight
  from Apple's CDN (`winget install Apple.AppleMobileDeviceSupport`,
  hash-verified), with the Store app as fallback — identical behaviour in the
  portable build and the installed build. The app owns the full lifecycle:
  detect → install → verify → repair.
