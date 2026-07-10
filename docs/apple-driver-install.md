# Apple iPhone Driver on Windows — Install Guide

PhotoAlbum needs the **Apple Mobile Device USB driver** to browse an iPhone's
photos. Windows' built-in generic MTP driver can *see* the phone but often
cannot browse it (Device Manager shows "requires further installation").

Apple's license forbids bundling the driver with PhotoAlbum, so it is
installed from Apple's own servers — automatically by the app's Phone-view
banner, or manually using the commands below.

## Automatic (what the app's "Install Driver" button runs)

```powershell
winget install --id Apple.AppleMobileDeviceSupport --exact --silent --accept-source-agreements --accept-package-agreements
```

- Downloads `AppleMobileDeviceSupport64.msi` from `swcdn.apple.com` (hash-verified by winget)
- Requires elevation — accept the UAC prompt
- Installs the Apple driver pair (`applekis.inf`, `applersm.inf`) and registers the **Apple Mobile Device Service**

## Manual install options

### Option 1 — winget, interactive (see progress/errors)

```powershell
winget install --id Apple.AppleMobileDeviceSupport --exact
```

Useful variants:

```powershell
winget search apple                                  # list available Apple packages
winget list --id Apple.AppleMobileDeviceSupport      # check if already installed
winget upgrade --id Apple.AppleMobileDeviceSupport   # update to latest
winget uninstall --id Apple.AppleMobileDeviceSupport # remove
```

### Option 2 — Apple Devices app (Microsoft Store)

The full Apple management app; includes the same driver:

```powershell
winget install --id 9NP83LWLPZ9K --source msstore --accept-source-agreements --accept-package-agreements
```

Or open the Store page directly: `ms-windows-store://pdp/?ProductId=9NP83LWLPZ9K`

### Option 3 — iTunes (legacy; also carries the driver)

```powershell
winget install --id Apple.iTunes --exact
```

## Verifying the install

```powershell
# Service registered?
sc query "Apple Mobile Device Service"

# Apple drivers in the Windows driver store?
pnputil /enum-drivers | Select-String -Context 2,4 "Apple"

# Support files present?
dir "C:\Program Files\Common Files\Apple\Mobile Device Support"
```

Expected: two Apple, Inc. INFs (`applekis.inf`, `applersm.inf`) and the
service present. Note: the modern (2023+) driver stack no longer ships the
legacy `usbaapl64.sys` file — its absence is normal.

## After installing

1. **Unplug and replug** the iPhone (Windows re-binds it to the Apple driver;
   the Apple Mobile Device Service starts automatically — reboot once if the
   device still shows "requires further installation" in Device Manager).
2. **Unlock** the iPhone and tap **"Trust This Computer"** (enter the phone
   passcode). The phone must stay unlocked while browsing.
3. In PhotoAlbum, open the **Phone** view and press **Refresh**.

## Troubleshooting

| Symptom | Fix |
|---|---|
| Phone charges but never appears | Driver missing — run the winget install above |
| Phone appears, 0 photos | Locked or not trusted — unlock, tap Trust, keep unlocked, Refresh |
| "Requires further installation" persists after install | Replug the cable; if still shown, reboot |
| winget not found | Install "App Installer" from the Microsoft Store, or use Option 2 via the Store UI |
| DCIM shows but photos missing | iCloud "Optimize Storage" keeps originals in the cloud — on the phone: Settings → Photos → **Download and Keep Originals** |
