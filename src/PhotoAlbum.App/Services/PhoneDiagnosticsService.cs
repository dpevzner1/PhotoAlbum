using Microsoft.Win32;
using System.Diagnostics;
using System.IO;

namespace PhotoAlbum.App.Services;

public sealed record PhoneDiagnostics(
    bool DriverPresent,
    bool ServiceInstalled,
    bool ServiceRunning,
    bool WpdEnumRunning)
{
    public bool AllHealthy => DriverPresent && ServiceInstalled && ServiceRunning && WpdEnumRunning;
}

/// <summary>
/// Runs the Windows-side health checks for iPhone connectivity whenever a
/// phone connects: Apple driver present, Apple Mobile Device Service
/// installed and running. Mirrors the manual diagnostic procedure in
/// docs/apple-driver-install.md, and can start the service (UAC prompt).
/// </summary>
public sealed class PhoneDiagnosticsService
{
    private const string ServiceName = "Apple Mobile Device Service";
    private readonly AppleDriverService _driver;

    public PhoneDiagnosticsService(AppleDriverService driver) => _driver = driver;

    /// <summary>Run all checks and write one summary line to the run log.</summary>
    public PhoneDiagnostics Run()
    {
        var result = new PhoneDiagnostics(
            DriverPresent:    _driver.IsDriverPresent(),
            ServiceInstalled: IsServiceInstalled(),
            ServiceRunning:   IsServiceRunning(),
            WpdEnumRunning:   IsWindowsServiceRunning("WPDBusEnum"));

        RunLogger.Info("PhoneDiag",
            $"Driver={(result.DriverPresent ? "OK" : "MISSING")}  " +
            $"AppleService={(result.ServiceInstalled ? (result.ServiceRunning ? "RUNNING" : "STOPPED") : "NOT INSTALLED")}  " +
            $"WPDBusEnum={(result.WpdEnumRunning ? "RUNNING" : "STOPPED")}");
        return result;
    }

    /// <summary>Query any Windows service state via `sc query` (works for svchost-hosted services).</summary>
    public static bool IsWindowsServiceRunning(string serviceName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe", Arguments = $"query \"{serviceName}\"",
                UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public bool IsServiceInstalled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\{ServiceName}");
            return key is not null;
        }
        catch { return false; }
    }

    public bool IsServiceRunning()
    {
        try { return Process.GetProcessesByName("AppleMobileDeviceService").Length > 0; }
        catch { return false; }
    }

    /// <summary>
    /// Full connection repair — the same procedure that fixed the original
    /// bring-up, as one elevated action (single UAC prompt):
    ///   1. restart WPDBusEnum (Portable Device Enumerator)
    ///   2. restart the Apple Mobile Device Service
    ///   3. restart the iPhone USB device node (simulated replug → fresh
    ///      arrival event re-registers the phone with the enumerator)
    /// Returns true when the services are running afterwards.
    /// </summary>
    public async Task<bool> TryRepairAppleStackElevatedAsync(CancellationToken ct = default)
    {
        RunLogger.Action("PhoneDiag", "Repair Apple connection stack requested");
        var script = Path.Combine(Path.GetTempPath(), "photoalbum_repair_apple.ps1");
        await File.WriteAllTextAsync(script, """
            $ErrorActionPreference = 'SilentlyContinue'
            Stop-Service 'Apple Mobile Device Service' -Force
            Stop-Service 'WPDBusEnum' -Force
            Start-Sleep -Seconds 2
            Start-Service 'WPDBusEnum'
            Start-Service 'Apple Mobile Device Service'
            # Restart the iPhone composite device (children restart with it) —
            # equivalent to a physical replug, generating a fresh arrival event.
            Get-PnpDevice -PresentOnly |
                Where-Object { $_.InstanceId -like 'USB\VID_05AC*' -and $_.InstanceId -notmatch '&MI_' } |
                ForEach-Object { pnputil /restart-device "$($_.InstanceId)" }
            Start-Sleep -Seconds 2
            """, ct);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName        = "powershell.exe",
                Arguments       = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"",
                Verb            = "runas",       // UAC prompt
                UseShellExecute = true,
                WindowStyle     = ProcessWindowStyle.Hidden,
            };
            using var proc = Process.Start(psi);
            if (proc is not null) await proc.WaitForExitAsync(ct);
            await Task.Delay(4000, ct); // service + device settle time
            var ok = IsServiceRunning();
            RunLogger.Info("PhoneDiag", ok
                ? "Repair completed — Apple service running, device node restarted"
                : "Repair ran but Apple service is not running (UAC declined or start failed)");
            return ok;
        }
        catch (Exception ex)
        {
            RunLogger.Warn("PhoneDiag", "Repair failed/declined", ex);
            return false;
        }
        finally
        {
            try { File.Delete(script); } catch { }
        }
    }

    /// <summary>
    /// Start the Apple service via an elevated `sc start` (triggers UAC).
    /// Returns true when the service is running afterwards.
    /// </summary>
    public async Task<bool> TryStartServiceElevatedAsync(CancellationToken ct = default)
    {
        if (IsServiceRunning()) return true;
        RunLogger.Action("PhoneDiag", "Requesting elevated service start", ServiceName);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName        = "sc.exe",
                Arguments       = $"start \"{ServiceName}\"",
                Verb            = "runas",       // UAC prompt
                UseShellExecute = true,
                WindowStyle     = ProcessWindowStyle.Hidden,
            };
            using var proc = Process.Start(psi);
            if (proc is not null) await proc.WaitForExitAsync(ct);
            await Task.Delay(1500, ct); // service spin-up
            var running = IsServiceRunning();
            RunLogger.Info("PhoneDiag", running ? "Service started" : "Service did not start");
            return running;
        }
        catch (Exception ex)
        {
            // User declined UAC or start failed — non-fatal, photo browsing may still work.
            RunLogger.Warn("PhoneDiag", "Elevated service start failed/declined", ex);
            return false;
        }
    }
}
