using Microsoft.Extensions.DependencyInjection;
using PhotoAlbum.App.Services;
using PhotoAlbum.App.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PhotoAlbum.App.Views;

public partial class PhoneView : Page
{
    private readonly PhoneViewModel? _vm;

    public PhoneView()
    {
        InitializeComponent();
        if (Application.Current is App app && app.Services is { } sp)
        {
            _vm = sp.GetRequiredService<PhoneViewModel>();
            DataContext = _vm;
            _ = _vm.LoadCommand.ExecuteAsync(null);
        }
    }

    private async void PlayVideoBtn_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true; // don't toggle selection underneath
        if (_vm?.Device is null || sender is not FrameworkElement fe || fe.Tag is not PhoneItemVm item)
            return;

        // Preview requires pulling the bytes off the phone first (MTP cannot
        // stream). Confirm for large files.
        if (item.Item.SizeBytes > 200 * 1024 * 1024 &&
            MessageBox.Show($"This video is {item.SizeText}. Download a temporary copy to preview it?",
                "Large video", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        var app = (App)Application.Current;
        var devices = app.Services!.GetRequiredService<PhotoAlbum.Core.Interfaces.IDeviceService>();
        var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PhotoAlbum", "preview");
        System.IO.Directory.CreateDirectory(tempDir);
        var tempPath = System.IO.Path.Combine(tempDir, $"{Guid.NewGuid():N}_{item.Name}");

        try
        {
            RunLogger.Action("PhoneView", "Video preview download", $"{item.Name} ({item.SizeText})");
            _vm.StatusText = $"Downloading {item.Name} for preview…";
            await devices.DownloadItemAsync(_vm.Device.DeviceId, item.Item.ItemId, tempPath);
            _vm.StatusText = $"Previewing {item.Name}";
            var win = new VideoPreviewWindow(tempPath, item.Name, deleteOnClose: true)
                { Owner = Window.GetWindow(this) };
            win.Show();
        }
        catch (Exception ex)
        {
            try { System.IO.File.Delete(tempPath); } catch { }
            _vm.StatusText = $"Preview failed: {ex.Message}";
            RunLogger.Warn("PhoneView", "Video preview failed", ex);
        }
    }

    private void Item_Click(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null || sender is not FrameworkElement fe || fe.DataContext is not PhoneItemVm item)
            return;
        item.IsSelected = !item.IsSelected;
        _vm.RecountSelection();
    }

    private void BrowseDestBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose backup destination",
            InitialDirectory = System.IO.Directory.Exists(_vm.Destination) ? _vm.Destination : null,
        };
        if (dlg.ShowDialog() != true) return;

        var (ok, error) = PhoneBackupService.ValidateDestination(dlg.FolderName);
        if (!ok)
        {
            MessageBox.Show(error, "Folder not usable", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _vm.Destination = dlg.FolderName;
        RunLogger.Action("PhoneView", "Backup destination chosen", dlg.FolderName);
    }
}
