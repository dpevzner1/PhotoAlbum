using Microsoft.Extensions.DependencyInjection;
using PhotoAlbum.App.Services;
using PhotoAlbum.Core.Domain;
using PhotoAlbum.Core.Interfaces;
using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PhotoAlbum.App.Views;

// Thin display wrapper for IndexedFolder
file sealed class FolderLocationVm(IndexedFolder f)
{
    public string FolderPath     { get; } = f.FolderPath;
    public long   FileCount      { get; } = f.FileCount;
    public DateTime LastIndexedUtc { get; } = f.LastIndexedUtc;
    public string FormattedSize  { get; } = FormatBytes(f.TotalSizeBytes);

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
        >= 1_048_576     => $"{bytes / 1_048_576.0:F1} MB",
        >= 1_024         => $"{bytes / 1_024.0:F1} KB",
        _                => $"{bytes} B"
    };
}

public partial class SettingsView : Page
{
    private UserSettingsService? _settings;
    private IIndexedFolderRepository? _folderRepo;
    private bool _loading;

    public SettingsView()
    {
        InitializeComponent();
        if (System.Windows.Application.Current is App app && app.Services is { } sp)
        {
            _settings = sp.GetRequiredService<UserSettingsService>();
            _folderRepo = sp.GetRequiredService<IIndexedFolderRepository>();
            _ = LoadSettingsAsync();
        }
        Loaded += (_, _) => CheckApiStatus_Click(this, new RoutedEventArgs());
    }

    private async Task LoadSettingsAsync()
    {
        if (_settings is null) return;
        _loading = true;
        try
        {
            var theme = await _settings.GetThemeAsync();
            ApplyTheme(theme);

            ThemeCombo.SelectedItem = ThemeCombo.Items
                .Cast<ComboBoxItem>()
                .FirstOrDefault(i => i.Tag?.ToString() == theme)
                ?? ThemeCombo.Items[2];

            var exifOn = await _settings.GetExifWriteBackEnabledAsync();
            ExifWritebackToggle.IsOn = exifOn;

            await LoadFolderLocationsAsync();
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task LoadFolderLocationsAsync()
    {
        if (_folderRepo is null) return;
        var folders = await _folderRepo.GetAllAsync();
        if (FolderLocationsList is null || NoFoldersText is null) return;
        if (folders.Count == 0)
        {
            NoFoldersText.Visibility = Visibility.Visible;
            FolderLocationsList.Visibility = Visibility.Collapsed;
        }
        else
        {
            NoFoldersText.Visibility = Visibility.Collapsed;
            FolderLocationsList.Visibility = Visibility.Visible;
            FolderLocationsList.ItemsSource = folders.Select(f => new FolderLocationVm(f)).ToList();
        }
    }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _settings is null) return;
        if (ThemeCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            ApplyTheme(tag);
            _ = _settings.SetThemeAsync(tag);
        }
    }

    private static void ApplyTheme(string tag) => App.ApplySavedTheme(tag);

    private async void CheckApiStatus_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ApiStatusText is not null) ApiStatusText.Text = "Checking…";
            if (ApiStatusDot  is not null) ApiStatusDot.Fill  = new SolidColorBrush(Colors.Orange);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            _ = await http.GetAsync("http://127.0.0.1:5150/api/v1/media?pageSize=1");
            if (ApiStatusDot  is not null) ApiStatusDot.Fill  = new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10));
            if (ApiStatusText is not null) ApiStatusText.Text = "Running — http://127.0.0.1:5150";
        }
        catch
        {
            if (ApiStatusDot  is not null) ApiStatusDot.Fill  = new SolidColorBrush(Colors.Gray);
            if (ApiStatusText is not null) ApiStatusText.Text = "Not reachable (API may still be starting)";
        }
    }

    private void OpenDocs_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("http://127.0.0.1:5150/api/docs") { UseShellExecute = true });
    }

    private async void ExifWritebackToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _settings is null) return;
        await _settings.SetExifWriteBackEnabledAsync(ExifWritebackToggle.IsOn);
    }

    private void ApiUrl_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText("http://127.0.0.1:5150/api/v1/"); }
        catch { /* clipboard unavailable on some configurations */ }
        if (ApiCopiedText is not null) ApiCopiedText.Visibility = Visibility.Visible;
        _ = HideCopiedLabelAsync();
    }

    private async Task HideCopiedLabelAsync()
    {
        await Task.Delay(2000);
        ApiCopiedText.Visibility = Visibility.Collapsed;
    }
}
