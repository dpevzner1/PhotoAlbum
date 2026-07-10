using Microsoft.Win32;
using System.Diagnostics;

namespace PhotoAlbum.App.Services;

public sealed record PhoneDiagnostics(
    bool DriverPresent,
    bool ServiceInstalled,
    bool ServiceRunning)
{
    public bool AllHealthy => DriverPresent && ServiceInstalled && ServiceRunning;
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
            ServiceRunning:   IsServiceRunning());

        RunLogger.Info("PhoneDiag",
            $"Driver={(result.DriverPresent ? "OK" : "MISSING")}  " +
            $"Service={(result.ServiceInstalled ? (result.ServiceRunning ? "RUNNING" : "STOPPED") : "NOT INSTALLED")}");
        return result;
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
