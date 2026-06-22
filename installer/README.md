# Photo Album — Installer (chunked)

This folder contains the Windows installer split into parts so it fits within
GitHub's **100 MB per-file** limit. The full installer is a single ~104 MB
executable (`PhotoAlbum-Setup.exe`); here it is committed as numbered parts that
you reassemble locally.

## Install (end user)

1. Download every file in this folder (`PhotoAlbum-Setup.exe.001`,
   `.002`, …, plus `.sha256` and an assemble script).
2. Reassemble:
   - **Easiest:** double-click **`assemble.bat`** — it rebuilds the `.exe`,
     verifies its SHA-256, and offers to launch it.
   - **Or PowerShell:**
     ```powershell
     powershell -ExecutionPolicy Bypass -File assemble.ps1
     ```
3. Run **`PhotoAlbum-Setup.exe`** and follow the prompts (admin elevation is
   requested — it installs for all users).

The installer is a WiX **Burn** bootstrapper wrapping an MSI. It:

- Installs to `C:\Program Files\Photo Album`
- Writes `HKLM\Software\Antigrav\PhotoAlbum` (`InstallPath`, `Version`, `Publisher`)
- Registers in **Add/Remove Programs** (uninstall + repair)
- **Upgrades in place** when a newer build is run
- Enters **maintenance mode** (Repair / Remove) if re-run when already installed

Silent install is supported: `PhotoAlbum-Setup.exe /quiet` (or `/passive`),
uninstall with `/uninstall /quiet`, with logging via `/log <file>`.

## Files

| File | Purpose |
|------|---------|
| `PhotoAlbum-Setup.exe.001`, `.002`, … | Installer binary, split into < 95 MB parts |
| `PhotoAlbum-Setup.exe.sha256` | SHA-256 of the reassembled `.exe` (integrity check) |
| `assemble.bat` | Double-click reassembly + verify + launch |
| `assemble.ps1` | Cross-shell reassembly + verify |

## A note on distribution

Committing installer binaries to a Git repo (even chunked) bloats history
permanently. For ongoing releases prefer **GitHub Releases** (up to 2 GB per
asset, no chunking) or **Git LFS**. The chunked-in-repo layout here exists so
the installer ships *inside the repository tree* as requested; the `assemble`
scripts make reassembly a one-click step.
