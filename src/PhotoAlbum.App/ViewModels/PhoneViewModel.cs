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
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _backupCts;

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

    public ObservableCollection<PhoneItemVm> Items { get; } = [];

    public string TotalSizeText => $"{TotalBytes / 1_073_741_824.0:F2} GB";
    public string DeviceTitle => Device is null ? "No phone connected"
        : $"{Device.FriendlyName}{(string.IsNullOrEmpty(Device.Model) ? "" : $" ({Device.Model})")}";

    public PhoneViewModel(IDeviceService devices, IPhoneBackupService backup, DeviceWatcher watcher,
        AppleDriverService appleDriver)
    {
        _devices     = devices;
        _backup      = backup;
        _watcher     = watcher;
        _appleDriver = appleDriver;
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
        TotalCount = 0; TotalBytes = 0; SelectedCount = 0;
        DriverMissing = !_appleDriver.IsDriverPresent();

        if (Device is null) { StatusText = "Connect an iPhone via USB, unlock it, and tap “Trust This Computer”."; return; }

        IsLoading = true;
        StatusText = "Reading photos from device…";
        try
        {
            Destination = await _backup.GetSavedDestinationAsync(Device, ct);
            var index = await _backup.GetBackupIndexAsync(Device, ct);

            var progress = new Progress<int>(n => StatusText = $"Reading photos from device… {n} found");
            var media = await _devices.GetMediaItemsAsync(Device.DeviceId, progress, ct);

            foreach (var m in media.OrderByDescending(m => m.DateTaken ?? DateTime.MinValue))
            {
                // Size+name is a cheap pre-mark; exact dedup happens by hash at backup time.
                Items.Add(new PhoneItemVm(m));
                TotalBytes += m.SizeBytes;
            }
            TotalCount = Items.Count;
            OnPropertyChanged(nameof(TotalSizeText));
            StatusText = TotalCount == 0
                ? "No photos visible. Unlock the iPhone, tap “Trust This Computer” on it, keep it unlocked, then press Refresh. " +
                  "If it still shows nothing, install the Apple Devices app (or iTunes) so the Apple USB driver completes."
                : $"{TotalCount:N0} items · {TotalSizeText} on device · {index.Count:N0} previously backed up";

            _ = LoadThumbnailsAsync(ct); // fire-and-forget, cancelled on reload
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText = "Could not read the device. Make sure it is unlocked and trusted, then press Refresh.";
            RunLogger.Warn("PhoneView", "Device enumeration failed", ex);
        }
        finally { IsLoading = false; }
    }

    private async Task LoadThumbnailsAsync(CancellationToken ct)
    {
        if (Device is null) return;
        // Thumbnails come over MTP one at a time — cap the count so huge
        // libraries don't grind the device connection (v1 limitation).
        const int MaxThumbnails = 400;
        foreach (var vm in Items.Take(MaxThumbnails))
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                var bytes = await _devices.GetThumbnailAsync(Device.DeviceId, vm.Item.ItemId, ct);
                if (bytes is not null) vm.SetThumbnail(bytes);
            }
            catch { /* thumbnail is decorative — never fail the view */ }
        }
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var i in Items) i.IsSelected = true;
        SelectedCount = Items.Count;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var i in Items) i.IsSelected = false;
        SelectedCount = 0;
    }

    public void RecountSelection() => SelectedCount = Items.Count(i => i.IsSelected);

    [RelayCommand]
    private async Task BackupSelectedAsync()
    {
        if (Device is null || IsBackingUp) return;
        var selected = Items.Where(i => i.IsSelected).Select(i => i.Item).ToList();
        if (selected.Count == 0) { StatusText = "Select at least one item to back up."; return; }
        await RunBackupAsync(selected);
    }

    [RelayCommand]
    private async Task BackupAllAsync()
    {
        if (Device is null || IsBackingUp) return;
        await RunBackupAsync(Items.Select(i => i.Item).ToList());
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
                    ? $"Backing up {p.Done + 1}/{p.Total}: {p.CurrentFile}"
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
