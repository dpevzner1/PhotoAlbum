# iPhone Connect & Backup — Feasibility and Design

Status: **Approved for implementation** (feasibility research 2026-07; forum survey + DevOps engine review)

## Goal

When an iPhone is plugged in over USB, PhotoAlbum should:

1. Detect the connection and show a **Phone** item in the navigation rail (removed on disconnect).
2. Enumerate the phone's photos/videos and show relevant metrics (counts, sizes, dates, thumbnails).
3. Let the user back up **selected photos** or **entire device folders** to a **user-chosen destination folder**, with integrity verification.

## What public forums establish

### Transport and preconditions
- An iPhone appears to Windows as an **MTP/PTP "Portable Device"** (WPD subsystem), *not* a mass-storage drive. Raw `File.Copy` does not work; access goes through the WPD COM API.
- The device is only visible when it is **unlocked** and the user has accepted the **"Trust This Computer"** prompt. Locked/untrusted → invisible.
- The **Apple Mobile Device USB Driver** must be present (ships with iTunes or the Apple Devices app). Without it, the phone charges but never enumerates. Apple provides no standalone redistributable — we must **detect and guide**, never bundle.

### The hard ceiling — albums
- Over MTP/PTP (and libimobiledevice's AFC "jailed" Media area) the phone exposes **only `DCIM/1xxAPPLE` camera-roll files** plus per-file metadata.
- **Album structure (Favorites, user albums, Screenshots, etc.) lives in `Photos.sqlite` on the device and is NOT reachable** by any supported Windows interface. Forensic tooling reads it only from full device backups.
- Conclusion: **iPhone album mirroring is out of scope.** Mitigation: "back up new since last sync" (BLAKE3 diff) + re-album inside PhotoAlbum after import.

### Component landscape (from forums)
| Component | Role | Notes |
|---|---|---|
| `MediaDevices` (NuGet, Bassman2) | Managed WPD/MTP wrapper: enumerate, properties, thumbnails, download | Primary path. Single-maintainer — pin version |
| `imobiledevice-net` | Optional: UDID/pairing/battery via lockdownd | LGPL — dynamic link only; defer to v2 |
| `RegisterDeviceNotification` + `WM_DEVICECHANGE` | Connect/disconnect "sensor" | Hook via `HwndSource`; handle async off the PnP thread; poll WPD list as fallback |
| Apple Mobile Device USB Driver | Hard runtime dependency | **Cannot be bundled** — Apple's SLA forbids redistribution ("may not ... redistribute or sublicense"). Instead the app bootstraps it: `AppleDriverService` detects the driver (usbaapl64.sys / Apple Mobile Device Service) and installs on demand via `winget install Apple.AppleMobileDeviceSupport` (downloaded from Apple's servers — license flows from Apple to the user), with the Apple Devices Store page as fallback. Same code path covers portable and installed deployments; the installer deliberately carries no Apple payload (DevOps review: compliance + supply-chain — never freeze a third-party signed driver into our MSI) |
| HEIC codec | Decode iPhone photos | Already handled (Store guidance + Rust thumbnailer) |

## Feasibility verdict

| Capability | Verdict |
|---|---|
| Detect connect/disconnect, dynamic Phone nav item | 🟢 Feasible |
| Enumerate photos/videos + metrics (count, size, dates, thumbnails) | 🟢 Feasible |
| Back up selected photos / folders with verified copy | 🟢 Feasible (reuse Rust BLAKE3 verified copy engine) |
| User-selected backup destination | 🟢 Feasible (`OpenFolderDialog`, persisted in UserSettings) |
| Mirror iPhone albums | 🔴 Not feasible via supported interfaces — descoped |

## DevOps engine review (applied directives)

- **Architecture / boundaries:** all device access behind a Core interface (`IDeviceService`); the MediaDevices adapter lives in App/Infrastructure. No MTP types leak into ViewModels. Same pattern as `IRust*`.
- **Least privilege / security:** device access is **read-only** — we never write to the phone. The Trust prompt is the consent gate. Backup destination must default to a **user-writable** location; warn if the user picks a non-writable path.
- **Program Files gotcha:** the installed app lives in `C:\Program Files\Photo Album`, which is not writable without elevation. "Nested in the application's working folder" therefore defaults to `%USERPROFILE%\PhotoAlbum\Backups\<Device>\` — the picker allows any writable folder, including under the app folder for portable installs. The picker validates writability before accepting.
- **Reliability / error handling:** MTP disconnects mid-transfer, devices sleep and re-lock. Every transfer is: copy → BLAKE3 verify → operation-log. Device-removed-mid-operation must surface a clear state and leave no partial files (write to `.part`, rename on verify).
- **Delivery / validation:** no real hardware in CI → `IDeviceService` gets a mock implementation for tests; manual test matrix across iOS versions; re-verify on major iOS releases (Apple periodically changes MTP behavior).

## Design

### New components

```
PhotoAlbum.Core/
  Interfaces/IDeviceService.cs      # device + media enumeration + download contract
  Domain/PhoneDevice.cs             # FriendlyName, Model, SerialId, connection state
  Domain/PhoneMediaItem.cs          # DeviceItemId, Name, Folder, SizeBytes, DateTaken, IsVideo

PhotoAlbum.App/
  Services/DeviceWatcher.cs         # WM_DEVICECHANGE hook + WPD poll fallback → events
  Services/PhoneBackupService.cs    # selection → verified copy → operation log
  Infrastructure/MtpDeviceService.cs# MediaDevices adapter (implements IDeviceService)
  ViewModels/PhoneViewModel.cs
  Views/PhoneView.xaml(.cs)
  AppShell: dynamic "Phone" RadioButton (Collapsed until DeviceWatcher fires)
```

### Backup flow
1. User selects photos (or folders) in PhoneView; picks destination (default `%USERPROFILE%\PhotoAlbum\Backups\<Device>\<yyyy-MM-dd>`; persisted per device in UserSettings).
2. For each item: download from device to `<dest>\<name>.part` → BLAKE3 hash → compare with a re-read → rename to final name → operation log entry.
3. "Skip already backed up": hash-index of prior backups per device; items whose hash exists are marked and skippable.
4. Optional "Import into library after backup" toggle → runs the existing IndexOrchestrator on the destination.

### Failure modes handled
- Driver missing → Phone nav shows a guidance banner (install Apple Devices / iTunes).
- Device locked/untrusted → "Unlock your iPhone and tap Trust" state screen.
- Disconnect mid-transfer → transfer aborts, `.part` files deleted, partial batch recorded in operation log; reconnect resumes by hash diff.
- HEIC codec missing → existing banner.

### API exposure (per project rule: all features API-accessible)
- `GET  /api/v1/phone/status` — connected device info or `{connected:false}`
- `GET  /api/v1/phone/media` — enumerated items (paged)
- `POST /api/v1/phone/backup` — `{ itemIds:[], destination:"..." }` → job id
- `GET  /api/v1/phone/backup/{jobId}` — progress/result

## See also

- [apple-driver-install.md](apple-driver-install.md) — winget commands and manual install steps for the Apple USB driver

## Sources

- CopyTrans — DCIM folder structure & MTP limitations: https://copytrans.studio/support/dcim-folder-guide/
- Microsoft Q&A — iPhone transfer behavior on Windows: https://learn.microsoft.com/en-us/answers/questions/4037211/transfer-from-iphone-to-pc-doesnt-work-correctly
- MediaDevices library: https://github.com/Bassman2/MediaDevices / https://www.nuget.org/packages/MediaDevices/
- libimobiledevice / imobiledevice-net: https://libimobiledevice.org/ / https://github.com/libimobiledevice-win32/imobiledevice-net
- Photos.sqlite internals (why albums are unreachable): https://theforensicscooter.com/2022/05/02/photos-sqlite-query-documentation-notable-artifacts/
- USB device notification in C#: https://community.silabs.com/s/article/detecting-when-a-usb-device-is-connected-or-removed-in-c-net?language=en_US
- Apple Mobile Device USB Driver: https://www.systweak.com/blogs/how-to-download-apple-mobile-device-usb-driver/
