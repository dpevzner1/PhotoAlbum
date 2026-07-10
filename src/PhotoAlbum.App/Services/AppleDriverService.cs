using Microsoft.Win32;
using System.Diagnostics;
using System.IO;

namespace PhotoAlbum.App.Services;

/// <summary>
/// Detects the Apple Mobile Device USB driver and installs it on demand.
///
/// Apple's license forbids redistributing the driver inside our installer
/// ("may not ... redistribute or sublicense"), so we bootstrap it instead:
/// winget pulls Apple.AppleMobileDeviceSupport straight from Apple's servers —
/// the user receives the bits and license from Apple, not from us.
/// Fallback: the Apple Devices app page in the Microsoft Store.
/// See docs/iphone-backup-feasibility.md.
/// </summary>
public sealed class AppleDriverService
{
    private const string WingetPackageId = "Apple.AppleMobileDeviceSupport";
    private const string AppleDevicesStoreUri = "ms-windows-store://pdp/?ProductId=9NP83LWLPZ9K";

    /// <summary>True when the Apple Mobile Device USB driver appears installed.</summary>
    public bool IsDriverPresent()
    {
        try
        {
            // The USB driver itself
            var sys = Path.Combine(Environment.SystemDirectory, "drivers", "usbaapl64.sys");
            if (File.Exists(sys)) return true;

            // The companion service registration (present with iTunes / Apple Devices)
            using var svc = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\Apple Mobile Device Service");
            return svc is not null;
        }
        catch { return false; } // registry unreadable — assume missing, banner is harmless
    }

    /// <summary>True when winget is available on this machine.</summary>
    public bool IsWingetAvailable()
    {
        try
        {
            var path = Environment.ExpandEnvironmentVariables(
                @"%LOCALAPPDATA%\Microsoft\WindowsApps\winget.exe");
            return File.Exists(path);
        }
        catch { return false; }
    }

    /// <summary>
    /// Install the driver via winget (downloads from Apple; UAC prompt appears).
    /// Returns (success, user-facing message).
    /// </summary>
    public async Task<(bool Ok, string Message)> InstallDriverAsync(CancellationToken ct = default)
    {
        if (!IsWingetAvailable())
        {
            OpenStorePage();
            return (false, "winget is not available — opened the Microsoft Store page for the Apple Devices app instead.");
        }

        RunLogger.Action("AppleDriver", "winget install started", WingetPackageId);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName  = "winget",
                Arguments = $"install --id {WingetPackageId} --exact --silent " +
                            "--accept-source-agreements --accept-package-agreements",
                UseShellExecute = false,
                CreateNoWindow  = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            using var proc = Process.Start(psi)!;
            var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode == 0)
            {
                RunLogger.Info("AppleDriver", "winget install succeeded");
                return (true, "Apple driver installed. Unplug and reconnect the iPhone, then press Refresh.");
            }

            RunLogger.Warn("AppleDriver", $"winget exited {proc.ExitCode}: {Tail(stdout)}");
            OpenStorePage();
            return (false, "Automatic install did not complete — opened the Microsoft Store page for the Apple Devices app. " +
                           "Install it there, then reconnect the iPhone.");
        }
        catch (Exception ex)
        {
            RunLogger.Warn("AppleDriver", "winget install failed", ex);
            OpenStorePage();
            return (false, "Automatic install failed — opened the Microsoft Store page for the Apple Devices app instead.");
        }
    }

    /// <summary>Open the Apple Devices app page in the Microsoft Store.</summary>
    public void OpenStorePage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppleDevicesStoreUri) { UseShellExecute = true });
            RunLogger.Action("AppleDriver", "Opened Microsoft Store page for Apple Devices");
        }
        catch (Exception ex)
        {
            RunLogger.Warn("AppleDriver", "Could not open Store page", ex);
        }
    }

    private static string Tail(string s) =>
        s.Length <= 300 ? s.Trim() : s[^300..].Trim();
}
