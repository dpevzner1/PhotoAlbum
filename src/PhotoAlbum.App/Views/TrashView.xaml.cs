using Microsoft.Extensions.DependencyInjection;
using PhotoAlbum.Core.Domain;
using PhotoAlbum.Core.Interfaces;
using System.Windows;
using System.Windows.Controls;

namespace PhotoAlbum.App.Views;

public partial class TrashView : Page
{
    private IMediaItemRepository? _mediaRepo;
    private IOperationLogRepository? _opLog;
    private List<MediaItem> _items = [];

    public TrashView()
    {
        InitializeComponent();
        if (System.Windows.Application.Current is App app && app.Services is { } sp)
        {
            _mediaRepo = sp.GetRequiredService<IMediaItemRepository>();
            _opLog = sp.GetRequiredService<IOperationLogRepository>();
        }
        TrashList.SelectionChanged += TrashList_SelectionChanged;
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_mediaRepo is null) return;

        _items = (await _mediaRepo.QueryAsync(new MediaFilter(OnlyDeleted: true, PageSize: 1000)))
            .ToList();

        TrashList.ItemsSource = _items;
        CountText.Text = $"Trash — {_items.Count} item{(_items.Count == 1 ? "" : "s")}";
        EmptyState.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectionBar.Visibility = Visibility.Collapsed;
    }

    private void TrashList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var count = TrashList.SelectedItems.Count;
        SelectionBar.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectionCountText.Text = $"{count} selected";
    }

    private async void RestoreBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_mediaRepo is null) return;
        var ids = TrashList.SelectedItems.Cast<MediaItem>().Select(m => m.Id).ToList();
        foreach (var id in ids)
        {
            await _mediaRepo.RestoreAsync(id);
            await _opLog!.LogAsync("Restore", "MediaItem", id, null);
        }
        await RefreshAsync();
    }

    private async void DeletePermanentlyBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_mediaRepo is null) return;
        var selected = TrashList.SelectedItems.Cast<MediaItem>().ToList();
        var confirm = MessageBox.Show(
            $"Permanently delete {selected.Count} photo(s)? This cannot be undone.",
            "Confirm Permanent Delete", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        foreach (var item in selected)
            await _mediaRepo.HardDeleteAsync(item.Id);

        await RefreshAsync();
    }

    private async void EmptyTrashBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_mediaRepo is null || _items.Count == 0) return;
        var confirm = MessageBox.Show(
            $"Permanently delete all {_items.Count} items in trash? This cannot be undone.",
            "Empty Trash", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        foreach (var item in _items)
            await _mediaRepo.HardDeleteAsync(item.Id);

        await RefreshAsync();
    }
}
