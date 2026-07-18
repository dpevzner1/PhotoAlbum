using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoAlbum.App.Services;
using PhotoAlbum.Core.Domain;
using PhotoAlbum.Core.Interfaces;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Imaging;

namespace PhotoAlbum.App.ViewModels;

public sealed partial class PhoneItemVm : ObservableObject
{
    public PhoneMediaItem Item { get; }
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private BitmapImage? _thumbnail;

    public PhoneItemVm(PhoneMediaItem item) => Item = item;

    public string Name => Item.Name;
    public string Folder => Item.Folder;
    public string SizeText => Item.SizeBytes >= 1_048_576
        ? $"{Item.SizeBytes / 1_048_576.0:F1} MB" : $"{Item.SizeBytes / 1024.0:F0} KB";
    public string DateText => Item.DateTaken?.ToString("yyyy-MM-dd HH:mm") ?? "";
    public bool IsVideo => Item.IsVideo;
    public bool AlreadyBackedUp => Item.AlreadyBackedUp;

    public void SetThumbnail(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.DecodePixelWidth = 160;
        bmp.EndInit();
        bmp.Freeze();
        Thumbnail = bmp;
    }
}

public sealed partial class PhoneViewModel : ObservableObject
{
    private readonly IDeviceService _devices;
    private readonly IPhoneBackupService _backup;
    private readonly DeviceWatcher _watcher;
    private readonly AppleDriverService _appleDriver;
    private readonly PhoneDiagnosticsService _diagnostics;
    private readonly IUserSettingsRepository _userSettings;
    private readonly PhoneInventoryService _inventory;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _backupCts;

    private const string LastCountKeyPrefix = "phone.last_scan_count.";

    [ObservableProperty] private PhoneDevice? _device;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isBackingUp;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _destination = "";
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private long _totalBytes;
    [ObservableProperty] private double _backupProgress;   // 0..1
    [ObservableProperty] private bool _hideBackedUp;
    [ObservableProperty] private bool _driverMissing;
    [ObservableProperty] private bool _driverInstalling;
    [ObservableProperty] private bool _serviceStopped;

    // Live scan metrics (loading overlay)
    [ObservableProperty] private int _scanCount;
    [ObservableProperty] private string _scanBytesText = "";
    [ObservableProperty] private double _scanPct;            // 0..100, vs last-scan estimate
    [ObservableProperty] private bool _scanIndeterminate = true;
    [ObservableProperty] private string _scanHintText = "";

    // Device storage gauge (header)
    [ObservableProperty] private bool _hasStorageInfo;
    [ObservableProperty] private string _storageText = "";
    [ObservableProperty] private double _storageUsedPct;

    // The bound grid (Items) is CAPPED for rendering — WPF's WrapPanel does not
    // virtualize, so realizing tens of thousands of thumbnail tiles freezes the
    // UI thread. _allItems holds the COMPLETE library and is what backup and the
    // counters use, so nothing is lost by only rendering a page.
    // Exactly one page (<= PageSize tiles) is ever realized, so RAM stays flat
    // whether the library is 500 items or 500,000.
    private const int PageSize = 500;

    public ObservableCollection<PhoneItemVm> Items { get; } = [];
    private readonly List<PhoneItemVm> _allItems = [];

    // ── Paging state ──────────────────────────────────────────────────────────
    [ObservableProperty] private int _pageIndex;          // 0-based
    [ObservableProperty] private int _pageCount;
    [ObservableProperty] private bool _isPaged;           // more than one page
    [ObservableProperty] private string _pageText = "";
    public bool CanPrevPage => PageIndex > 0;
    public bool CanNextPage => PageIndex < PageCount - 1;

    // iPhone-style media type filter: 0=All, 1=Photos, 2=Videos
    [ObservableProperty] private int _typeFilterIndex;
    [ObservableProperty] private int _photoCount;
    [ObservableProperty] private int _videoCount;

    partial void OnTypeFilterIndexChanged(int value) { PageIndex = 0; ApplyTypeFilter(); }

    /// <summary>All items matching the current type filter (full set, not the current page).</summary>
    private IEnumerable<PhoneItemVm> FilteredAll() => _allItems.Where(vm =>
        TypeFilterIndex switch { 1 => !vm.IsVideo, 2 => vm.IsVideo, _ => true });

    private void ApplyTypeFilter()
    {
        var matched = FilteredAll().ToList();
        PageCount = Math.Max(1, (matched.Count + PageSize - 1) / PageSize);
        if (PageIndex >= PageCount) PageIndex = PageCount - 1;
        if (PageIndex < 0) PageIndex = 0;

        // Rebuild the bound collection with ONE reset (not N Adds) and only the
        // current page's items — the entire memory-safety guarantee.
        var page = matched.Skip(PageIndex * PageSize).Take(PageSize).ToList();
        Items.Clear();
        foreach (var vm in page) Items.Add(vm);

        IsPaged = matched.Count > PageSize;
        var from = matched.Count == 0 ? 0 : PageIndex * PageSize + 1;
        var to   = Math.Min((PageIndex + 1) * PageSize, matched.Count);
        PageText = IsPaged
            ? $"{from:N0}–{to:N0} of {matched.Count:N0}  (page {PageIndex + 1}/{PageCount})  —  “Back Up All” covers everything"
            : "";
        OnPropertyChanged(nameof(CanPrevPage));
        OnPropertyChanged(nameof(CanNextPage));
        RecountSelection();
        _ = LoadPageThumbnailsAsync();
    }

    [RelayCommand] private void NextPage()  { if (CanNextPage) { PageIndex++; ApplyTypeFilter(); } }
    [RelayCommand] private void PrevPage()  { if (CanPrevPage) { PageIndex--; ApplyTypeFilter(); } }
    [RelayCommand] private void FirstPage() { if (CanPrevPage) { PageIndex = 0; ApplyTypeFilter(); } }
    [RelayCommand] private void LastPage()  { if (CanNextPage) { PageIndex = PageCount - 1; ApplyTypeFilter(); } }

    public string TotalSizeText => $"{TotalBytes / 1_073_741_824.0:F2} GB";
    public string DeviceTitle => Device is null ? "No phone connected"
        : $"{Device.FriendlyName}{(string.IsNullOrEmpty(Device.Model) ? "" : $" ({Device.Model})")}";

    public PhoneViewModel(IDeviceService devices, IPhoneBackupService backup, DeviceWatcher watcher,
        AppleDriverService appleDriver, PhoneDiagnosticsService diagnostics,
        IUserSettingsRepository userSettings, PhoneInventoryService inventory)
    {
        _devices      = devices;
        _backup       = backup;
        _watcher      = watcher;
        _appleDriver  = appleDriver;
        _diagnostics  = diagnostics;
        _userSettings = userSettings;
        _inventory    = inventory;
    }

    [RelayCommand]
    private async Task RepairConnectionAsync()
    {
        StatusText = "Repairing the Apple connection stack — accept the Windows permission prompt…";
        var ok = await _diagnostics.TryRepairAppleStackElevatedAsync();
        StatusText = ok
            ? "Repair complete (services restarted, device re-registered). Rescanning…"
            : "Repair did not complete (prompt declined or service failed) — rescanning anyway…";
        await Task.Delay(2000);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task StartAppleServiceAsync()
    {
        StatusText = "Starting the Apple Mobile Device Service (accept the Windows permission prompt)…";
        var ok = await _diagnostics.TryStartServiceElevatedAsync();
        ServiceStopped = !ok && _diagnostics.IsServiceInstalled();
        StatusText = ok
            ? "Apple service started. Press Refresh."
            : "Service not started (prompt declined or failed). Photo browsing may still work — try Refresh, or reboot to auto-start it.";
    }

    [RelayCommand]
    private async Task InstallDriverAsync()
    {
        if (DriverInstalling) return;
        DriverInstalling = true;
        StatusText = "Installing the Apple driver (downloads from Apple — a Windows permission prompt may appear)…";
        try
        {
            var (ok, message) = await _appleDriver.InstallDriverAsync();
            StatusText = message;
            if (ok) DriverMissing = !_appleDriver.IsDriverPresent();
        }
        finally { DriverInstalling = false; }
    }

    [RelayCommand]
    private void OpenDriverStorePage() => _appleDriver.OpenStorePage();

    /// <summary>Bind to the first connected device and enumerate its media.</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        Device = _watcher.ConnectedDevices.FirstOrDefault();
        OnPropertyChanged(nameof(DeviceTitle));
        Items.Clear();
        _allItems.Clear();
        TotalCount = 0; TotalBytes = 0; SelectedCount = 0;
        PhotoCount = 0; VideoCount = 0;

        // Windows-side health checks — run on every connect/refresh.
        var diag = _diagnostics.Run();
        DriverMissing  = !diag.DriverPresent;
        ServiceStopped = (diag.ServiceInstalled && !diag.ServiceRunning) || !diag.WpdEnumRunning;

        if (Device is null) { StatusText = "Connect an iPhone via USB, unlock it, and tap “Trust This Computer”."; return; }

        IsLoading = true;
        StatusText = "Reading photos from device…";
        ScanCount = 0; ScanBytesText = ""; ScanPct = 0;
        try
        {
            Destination = await _backup.GetSavedDestinationAsync(Device, ct);
            var index = await _backup.GetBackupIndexAsync(Device, ct);

            // Device storage gauge — quick query before the scan starts.
            var storage = await _devices.GetStorageInfoAsync(Device.DeviceId, ct);
            if (storage is { CapacityBytes: > 0 })
            {
                HasStorageInfo = true;
                StorageText = $"{Gb(storage.UsedBytes)} used of {Gb(storage.CapacityBytes)}";
                StorageUsedPct = storage.UsedBytes * 100.0 / storage.CapacityBytes;
            }
            else HasStorageInfo = false;

            // INSTANT VIEW FROM CACHE: show the last sync's inventory immediately
            // so the grid isn't empty while the fresh scan runs. The scan below
            // is authoritative and replaces this once it completes.
            var cached = await _inventory.LoadInventoryAsync(Device.StableKey, ct);
            if (cached is { Count: > 0 })
            {
                foreach (var m in cached.OrderByDescending(m => m.DateTaken ?? DateTime.MinValue))
                    _allItems.Add(new PhoneItemVm(m));
                TotalCount = _allItems.Count;
                TotalBytes = cached.Sum(i => i.SizeBytes);
                PhotoCount = cached.Count(i => !i.IsVideo);
                VideoCount = cached.Count(i => i.IsVideo);
                ApplyTypeFilter(); // renders page 1 from cache (thumbnails from disk cache)
                OnPropertyChanged(nameof(TotalSizeText));
            }

            // Last scan's count = estimate for a determinate progress bar.
            var estRaw = await _userSettings.GetAsync(LastCountKeyPrefix + Device.StableKey, ct);
            int estimate = int.TryParse(estRaw, out var e) && e > 0 ? e : cached?.Count ?? 0;
            ScanIndeterminate = estimate == 0;
            ScanHintText = estimate > 0
                ? $"~{estimate:N0} items expected (from last scan)"
                : "First scan of this device — counting…";

            var progress = new Progress<PhoneScanProgress>(p =>
            {
                ScanCount = p.Count;
                ScanBytesText = p.Bytes >= 1_073_741_824
                    ? $"{p.Bytes / 1_073_741_824.0:F2} GB" : $"{p.Bytes / 1_048_576.0:F0} MB";
                if (estimate > 0)
                    ScanPct = Math.Min(99.0, p.Count * 100.0 / estimate);
                StatusText = $"Reading photos from device… {p.Count:N0} found · {ScanBytesText}";
            });
            var media = await _devices.GetMediaItemsAsync(Device.DeviceId, progress, ct);

            // Remember this scan's count for next time's progress estimate.
            await _userSettings.SetAsync(LastCountKeyPrefix + Device.StableKey, media.Count.ToString(), ct);

            // Fresh scan is authoritative. Compute how many are NEW since the
            // cached inventory (incremental sync signal), then rebuild + persist.
            var (_, addedSinceLast) = _inventory.Merge(cached, media);
            _allItems.Clear();
            TotalBytes = 0;
            foreach (var m in media.OrderByDescending(m => m.DateTaken ?? DateTime.MinValue))
            {
                // Size+name is a cheap pre-mark; exact dedup happens by hash at backup time.
                _allItems.Add(new PhoneItemVm(m));
                TotalBytes += m.SizeBytes;
            }
            TotalCount = _allItems.Count;
            PhotoCount = _allItems.Count(i => !i.IsVideo);
            VideoCount = _allItems.Count(i => i.IsVideo);
            ApplyTypeFilter(); // re-render current page + load its thumbnails
            OnPropertyChanged(nameof(TotalSizeText));

            // Persist the fresh inventory so next connect is instant.
            await _inventory.SaveInventoryAsync(Device.StableKey, media, ct);
            // The advertised total can legitimately exceed physical storage:
            // with iCloud "Optimize iPhone Storage", the phone advertises every
            // asset at its full ORIGINAL size and streams from iCloud on
            // transfer. Label honestly instead of confusing the user.
            bool exceedsDevice = storage is { CapacityBytes: > 0 } &&
                                 (ulong)TotalBytes > storage.CapacityBytes;
            StatusText = TotalCount == 0
                ? "No photos visible. Unlock the iPhone, tap “Trust This Computer” on it, keep it unlocked, then press Refresh. " +
                  "If it still shows nothing, install the Apple Devices app (or iTunes) so the Apple USB driver completes."
                : $"{TotalCount:N0} items · {TotalSizeText} library size · {index.Count:N0} previously backed up" +
                  (addedSinceLast > 0 && cached is { Count: > 0 } ? $" · {addedSinceLast:N0} new since last sync" : "") +
                  (exceedsDevice
                      ? "  —  larger than device storage: iCloud-optimized originals are included and download from iCloud during backup. " +
                        "For a faster full backup: iPhone Settings → Photos → Download and Keep Originals."
                      : "");
            // Page thumbnails load via ApplyTypeFilter → LoadPageThumbnailsAsync.
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText = "Could not read the device. Make sure it is unlocked and trusted, then press Refresh.";
            RunLogger.Warn("PhoneView", "Device enumeration failed", ex);
        }
        finally { IsLoading = false; }
    }

    private static string Gb(ulong bytes) => $"{bytes / 1_073_741_824.0:F1} GB";

    private CancellationTokenSource? _thumbCts;

    /// <summary>
    /// Load thumbnails for the CURRENT PAGE only. Cancels any prior page's load
    /// on navigation, so at most one page of MTP thumbnail fetches is ever in
    /// flight — bounded work, bounded memory. Disk cache makes revisits instant.
    /// </summary>
    private async Task LoadPageThumbnailsAsync()
    {
        _thumbCts?.Cancel();
        _thumbCts = new CancellationTokenSource();
        var ct = _thumbCts.Token;
        if (Device is null) return;

        // Snapshot the page's items so paging away can't mutate mid-load.
        var pageItems = Items.ToList();
        foreach (var vm in pageItems)
        {
            if (ct.IsCancellationRequested) return;
            if (vm.Thumbnail is not null) continue; // already loaded/cached this session

            // 1) Disk cache (persists across sessions) — instant, no MTP.
            var cached = await _inventory.TryLoadThumbnailAsync(Device.StableKey, vm.Item.ItemId, ct);
            if (cached is not null) { vm.SetThumbnail(cached); continue; }

            // 2) Fetch over MTP, then persist to the cache for next time.
            try
            {
                var bytes = await _devices.GetThumbnailAsync(Device.DeviceId, vm.Item.ItemId, ct);
                if (bytes is not null)
                {
                    vm.SetThumbnail(bytes);
                    _ = _inventory.SaveThumbnailAsync(Device.StableKey, vm.Item.ItemId, bytes);
                }
            }
            catch { /* thumbnail is decorative — never fail the view */ }
        }
    }

    [RelayCommand]
    private void SelectAll()
    {
        // Selects the ENTIRE filtered library, not just the rendered slice.
        foreach (var i in FilteredAll()) i.IsSelected = true;
        SelectedCount = FilteredAll().Count();
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var i in _allItems) i.IsSelected = false;
        SelectedCount = 0;
    }

    public void RecountSelection() => SelectedCount = _allItems.Count(i => i.IsSelected);

    [RelayCommand]
    private async Task BackupSelectedAsync()
    {
        if (Device is null || IsBackingUp) return;
        var selected = _allItems.Where(i => i.IsSelected).Select(i => i.Item).ToList();
        if (selected.Count == 0) { StatusText = "Select at least one item to back up."; return; }
        await RunBackupAsync(selected);
    }

    [RelayCommand]
    private async Task BackupAllAsync()
    {
        if (Device is null || IsBackingUp) return;
        // The full filtered library — every item, not the capped display.
        await RunBackupAsync(FilteredAll().Select(i => i.Item).ToList());
    }

    [RelayCommand]
    private void CancelBackup() => _backupCts?.Cancel();

    private async Task RunBackupAsync(IReadOnlyList<PhoneMediaItem> items)
    {
        _backupCts = new CancellationTokenSource();
        IsBackingUp = true;
        BackupProgress = 0;
        RunLogger.Action("PhoneView", "Backup started", $"{items.Count} item(s) → {Destination}");
        try
        {
            var progress = new Progress<PhoneBackupProgress>(p =>
            {
                BackupProgress = p.Total == 0 ? 0 : (double)p.Done / p.Total;
                StatusText = p.CurrentFile.Length > 0
                    ? $"Backing up {p.Done + 1}/{p.Total}: {p.CurrentFile}  ·  " +
                      $"{p.Copied} copied · {p.Skipped} skipped · {p.Failed} failed · {p.Remaining} remaining"
                    : $"Finishing…";
            });
            var result = await _backup.BackupAsync(Device!, items, Destination, progress, _backupCts.Token);
            StatusText = $"Backup complete — {result.Copied} copied, {result.Skipped} already backed up" +
                         (result.Failed > 0 ? $", {result.Failed} failed" : "") +
                         $" · {result.TotalBytes / 1_048_576.0:F0} MB → {result.Destination}";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Backup cancelled — no partial files were left behind.";
        }
        catch (Exception ex)
        {
            StatusText = $"Backup failed: {ex.Message}";
            RunLogger.Error("PhoneView", "Backup failed", ex);
        }
        finally
        {
            IsBackingUp = false;
            BackupProgress = 0;
        }
    }
}
