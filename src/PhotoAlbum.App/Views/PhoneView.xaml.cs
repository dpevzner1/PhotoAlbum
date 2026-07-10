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
