using Microsoft.Extensions.DependencyInjection;
using PhotoAlbum.App.Services;
using PhotoAlbum.Core.Domain;
using PhotoAlbum.Core.Interfaces;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PhotoAlbum.App.Views;

file record AlbumDisplayVm(Album Album, bool IsHidden);

public partial class AlbumsView : Page
{
    private IAlbumRepository?    _repo;
    private AlbumService?        _albumSvc;
    private HiddenContentService? _hidden;

    public AlbumsView()
    {
        InitializeComponent();
        if (System.Windows.Application.Current is App app && app.Services is { } sp)
        {
            _repo     = sp.GetRequiredService<IAlbumRepository>();
            _albumSvc = sp.GetRequiredService<AlbumService>();
            _hidden   = sp.GetService<HiddenContentService>();

            if (_hidden is not null)
                _hidden.HiddenSetChanged += () => Dispatcher.Invoke(() => _ = LoadAsync());

            _ = LoadAsync();
        }
    }

    private async Task LoadAsync()
    {
        if (_repo is null) return;
        var albums = await _repo.GetAllAsync();
        var vms = albums
            .Select(a => new AlbumDisplayVm(a, _hidden?.IsAlbumHidden(a.Id) == true))
            .ToList();
        AlbumList.ItemsSource = vms;
        RunLogger.Info("AlbumsView", "Albums loaded", $"Count={albums.Count}");
    }

    private async void NewAlbumBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_albumSvc is null) return;
        RunLogger.Action("AlbumsView", "New Album button clicked");
        var dlg = new InputDialog("Album name:", "New Album", "My Album")
            { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Result)) return;
        var name = dlg.Result.Trim();
        RunLogger.Action("AlbumsView", "Creating album", $"Name=\"{name}\"");
        await _albumSvc.CreateAsync(name);
        await LoadAsync();
    }

    private void Album_DoubleClick(object sender, MouseButtonEventArgs e)
        => OpenSelected();

    private void AlbumList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Space)
            OpenSelected();
    }

    private void OpenSelected()
    {
        if (AlbumList.SelectedItem is AlbumDisplayVm vm)
        {
            RunLogger.Action("AlbumsView", "Opening album", $"AlbumId={vm.Album.Id}  Name=\"{vm.Album.Name}\"");
            NavigationService?.Navigate(new AlbumDetailView(vm.Album));
        }
    }

    private async void RenameAlbum_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Album album } || _repo is null) return;
        RunLogger.Action("AlbumsView", "Rename album clicked", $"AlbumId={album.Id}");
        var dlg = new InputDialog("New album name:", "Rename Album", album.Name)
            { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Result)) return;
        album.Name = dlg.Result.Trim();
        await _repo.UpdateAsync(album);
        RunLogger.Info("AlbumsView", "Album renamed", $"AlbumId={album.Id}  NewName=\"{album.Name}\"");
        await LoadAsync();
    }

    private async void ToggleHideAlbum_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Album album } || _hidden is null) return;
        if (_hidden.IsAlbumHidden(album.Id))
        {
            RunLogger.Action("AlbumsView", "Unhide album", $"AlbumId={album.Id}");
            await _hidden.UnhideAlbumAsync(album.Id);
        }
        else
        {
            RunLogger.Action("AlbumsView", "Hide album", $"AlbumId={album.Id}");
            await _hidden.HideAlbumAsync(album.Id);
        }
        // HiddenSetChanged fires → LoadAsync refreshes list
    }

    private async void DeleteAlbum_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Album album } || _repo is null) return;
        RunLogger.Action("AlbumsView", "Delete album clicked", $"AlbumId={album.Id}");
        var choice = MessageBox.Show(
            $"Delete album \"{album.Name}\"?\n\n" +
            "• OK — remove the album (photos stay in library)\n" +
            "• Cancel — keep the album",
            "Delete Album", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (choice != MessageBoxResult.OK) return;
        await _repo.DeleteAsync(album.Id);
        RunLogger.Info("AlbumsView", "Album deleted", $"AlbumId={album.Id}");
        await LoadAsync();
    }

    private void DuplicatesSmartAlbumBtn_Click(object sender, RoutedEventArgs e)
    {
        RunLogger.Action("AlbumsView", "Navigating to Duplicates view");
        NavigationService?.Navigate(new DuplicatesView());
    }

    private void AlbumCard_Click(object sender, MouseButtonEventArgs e)
    {
        var src = e.OriginalSource as DependencyObject;
        while (src is not null) { if (src is Button) return; src = VisualTreeHelper.GetParent(src); }
        if (sender is not FrameworkElement { DataContext: AlbumDisplayVm vm }) return;
        RunLogger.Action("AlbumsView", "Album card clicked — open album", $"AlbumId={vm.Album.Id}");
        NavigationService?.Navigate(new AlbumDetailView(vm.Album));
    }
}
