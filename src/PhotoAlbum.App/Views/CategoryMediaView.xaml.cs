using Microsoft.Extensions.DependencyInjection;
using PhotoAlbum.App.ViewModels;
using PhotoAlbum.Core.Interfaces;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PhotoAlbum.App.Views;

public partial class CategoryMediaView : Page
{
    private readonly Func<IServiceProvider, Task<IReadOnlyList<long>>> _loadIds;
    private List<MediaItemVm> _vms = [];

    public CategoryMediaView(string title, Func<IServiceProvider, Task<IReadOnlyList<long>>> loadIds)
    {
        InitializeComponent();
        _loadIds = loadIds;
        TitleText.Text = title;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (Application.Current is not App app || app.Services is not { } sp) return;
        LoadingPanel.Visibility = Visibility.Visible;
        EmptyPanel.Visibility   = Visibility.Collapsed;

        try
        {
            var mediaRepo = sp.GetRequiredService<IMediaItemRepository>();
            var ids = await _loadIds(sp);
            var vms = new List<MediaItemVm>(ids.Count);
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
            _vms = vms;
            PhotoGrid.ItemsSource = vms;
            CountText.Text = $"{vms.Count} photo{(vms.Count == 1 ? "" : "s")}";
            EmptyPanel.Visibility = vms.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void BackBtn_Click(object sender, RoutedEventArgs e)
        => NavigationService?.GoBack();

    private void Thumb_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MediaItemVm vm }) return;
        var idx = _vms.IndexOf(vm);
        if (idx < 0) return;
        DetailView.NavigationContext = _vms.Select(v => (v.Id, v.ThumbnailPath ?? "")).ToList();
        DetailView.NavigationIndex   = idx;
        DetailView.OriginPage        = this;
        NavigationService?.Navigate(new DetailView(vm.Id, vm.ThumbnailPath ?? ""));
    }
}
