# Building Photo Album from source

## Prerequisites

| Tool | Version | Notes |
|------|---------|-------|
| Windows | 10 / 11 x64 | WPF target is `net10.0-windows` |
| .NET SDK | 10.0.x | https://dotnet.microsoft.com/download |
| Rust | 1.96+ stable | `x86_64-pc-windows-msvc` (or `-gnu`) |
| WiX Toolset | 5.0 | only needed to build the installer |

## 1. Build the Rust native core

The app loads `photoalbum_core.dll` via P/Invoke. Build it first:

```powershell
cd rust/photoalbum_core
cargo build --release
```

The `.csproj` copies the resulting DLL into the app output automatically.

## 2. Build and run the app

```powershell
dotnet restore PhotoAlbum.slnx
dotnet build PhotoAlbum.slnx -c Release
dotnet run --project src/PhotoAlbum.App/PhotoAlbum.App.csproj -c Release
```

A self-contained build is also copied to `COMPILED/` for quick manual testing
(`COMPILED/PhotoAlbum.App.exe`).

## 3. Run the tests

```powershell
dotnet test PhotoAlbum.slnx -c Release
```

## 4. Build the installer (optional)

Requires the WiX CLI and extensions:

```powershell
dotnet tool install --global wix
wix extension add -g WixToolset.UI.wixext
wix extension add -g WixToolset.BootstrapperApplications.wixext/5.0.0

cd installer
./build.ps1
```

This produces `PhotoAlbum-Setup.exe` (a Burn bootstrapper wrapping the MSI).
See [`installer/README.md`](installer/README.md) for the chunked-distribution
workflow used to keep the binary within GitHub's 100 MB per-file limit.

## Logs

The app writes logs to a `log/` folder next to the executable
(`AppContext.BaseDirectory`). On a crash it also emits a `crash-preinit-*.txt`
or a `FATAL` crash dump in `albumlog.md`.
