using Microsoft.Extensions.DependencyInjection;
using PhotoAlbum.App.Services;
using PhotoAlbum.App.ViewModels;
using PhotoAlbum.Core.Domain;
using PhotoAlbum.Core.Interfaces;
using System.Windows;
using System.Windows.Controls;

namespace PhotoAlbum.App.Views;

public partial class AlbumDetailView : Page
{
    private Album _album;

    public AlbumDetailView(Album album)
    {
        InitializeComponent();
        _album = album;
        AlbumTitle.Text = album.Name;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (System.Windows.Application.Current is not App app || app.Services is not { } sp) return;
        var albumRepo = sp.GetRequiredService<IAlbumRepository>();
        var mediaRepo = sp.GetRequiredService<IMediaItemRepository>();

        var ids = await albumRepo.GetMediaIdsAsync(_album.Id);
        var vms = new List<MediaItemVm>();
        foreach (var id in ids)
        {
            var item = await mediaRepo.GetByIdAsync(id);
            if (item is not null)
            {
                var vm = new MediaItemVm(item);
                vm.LoadThumbnail();
                vms.Add(vm);
            }
        }
        PhotoGrid.ItemsSource = vms;
    }

    private void BackBtn_Click(object sender, RoutedEventArgs e)
        => NavigationService?.GoBack();

    private async void RenameAlbumBtn_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current is not App app || app.Services is not { } sp) return;
        RunLogger.Action("AlbumDetailView", "Rename album clicked", $"AlbumId={_album.Id}");
        var dlg = new InputDialog("New album name:", "Rename Album", _album.Name)
            { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Result)) return;
        _album.Name = dlg.Result.Trim();
        var albumRepo = sp.GetRequiredService<IAlbumRepository>();
        await albumRepo.UpdateAsync(_album);
        AlbumTitle.Text = _album.Name;
        RunLogger.Info("AlbumDetailView", "Album renamed", $"AlbumId={_album.Id}  Name=\"{_album.Name}\"");
    }

    private async void RemoveFromAlbum_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MediaItemVm vm }) return;
        if (Application.Current is not App app || app.Services is not { } sp) return;
        RunLogger.Action("AlbumDetailView", "Remove from album clicked",
            $"AlbumId={_album.Id}  MediaItemId={vm.Id}");
        var albumRepo = sp.GetRequiredService<IAlbumRepository>();
        await albumRepo.RemoveMediaAsync(_album.Id, vm.Id);
        RunLogger.Info("AlbumDetailView", "Photo removed from album",
            $"AlbumId={_album.Id}  MediaItemId={vm.Id}");
        await LoadAsync();
    }
}
