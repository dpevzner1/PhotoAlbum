using PhotoAlbum.Core.Domain;
using PhotoAlbum.Core.Interfaces;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace PhotoAlbum.App.Services;

/// <summary>
/// Watches for phone connect/disconnect. Primary signal: WM_DEVICECHANGE
/// broadcasts (DBT_DEVNODES_CHANGED) on the main window, debounced, followed
/// by a WPD re-enumeration. Fallback: a slow poll timer, because MTP arrival
/// broadcasts are not 100% reliable (see docs/iphone-backup-feasibility.md).
/// </summary>
public sealed class DeviceWatcher
{
    private const int WM_DEVICECHANGE     = 0x0219;
    private const int DBT_DEVNODES_CHANGED = 0x0007;
    private const int DBT_DEVICEARRIVAL    = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;

    private readonly IDeviceService _devices;
    private readonly DispatcherTimer _debounce;
    private readonly DispatcherTimer _fallbackPoll;
    private bool _refreshRunning;

    /// <summary>Current device list; updated after every refresh.</summary>
    public IReadOnlyList<PhoneDevice> ConnectedDevices { get; private set; } = [];

    /// <summary>Raised on the UI thread whenever the connected device set changes.</summary>
    public event Action<IReadOnlyList<PhoneDevice>>? DevicesChanged;

    public DeviceWatcher(IDeviceService devices)
    {
        _devices = devices;

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = RefreshAsync(); };

        _fallbackPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _fallbackPoll.Tick += (_, _) => _ = RefreshAsync();
    }

    /// <summary>Hook the window's message loop. Call once after the main window is sourced.</summary>
    public void Attach(Window window)
    {
        var source = (HwndSource?)PresentationSource.FromVisual(window);
        if (source is null)
        {
            window.SourceInitialized += (_, _) => Attach(window);
            return;
        }
        source.AddHook(WndProc);
        _fallbackPoll.Start();
        _ = RefreshAsync(); // initial state (phone may already be plugged in)
        RunLogger.Info("DeviceWatcher", "Attached to window message loop; watching for phones");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DEVICECHANGE)
        {
            var evt = wParam.ToInt64();
            if (evt is DBT_DEVNODES_CHANGED or DBT_DEVICEARRIVAL or DBT_DEVICEREMOVECOMPLETE)
            {
                // Never block the PnP broadcast: just (re)start the debounce timer.
                _debounce.Stop();
                _debounce.Start();
            }
        }
        return IntPtr.Zero;
    }

    private async Task RefreshAsync()
    {
        if (_refreshRunning) return;
        _refreshRunning = true;
        try
        {
            var current = await _devices.GetConnectedDevicesAsync();
            var changed = current.Count != ConnectedDevices.Count
                       || current.Select(d => d.DeviceId).Except(ConnectedDevices.Select(d => d.DeviceId)).Any();
            ConnectedDevices = current;
            if (changed)
            {
                RunLogger.Info("DeviceWatcher",
                    current.Count > 0
                        ? $"Device set changed — {current.Count} device(s): {string.Join(", ", current.Select(d => d.FriendlyName))}"
                        : "Device set changed — no devices connected");
                DevicesChanged?.Invoke(current);
            }
        }
        catch (Exception ex)
        {
            RunLogger.Warn("DeviceWatcher", "Device refresh failed (non-fatal)", ex);
        }
        finally { _refreshRunning = false; }
    }
}
