using PhotoAlbum.App.Services;
using PhotoAlbum.App.ViewModels;
using System.Windows;
using System.Windows.Input;

namespace PhotoAlbum.App.Views;

public partial class BulkEditDialog : Window
{
    private readonly BulkEditViewModel _vm;

    public BulkEditDialog(BulkEditViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        RunLogger.Info("BulkEditDialog", "Dialog opened",
            $"SelectionCount={vm.SelectionCount}");
        _ = vm.LoadAsync();
    }

    private async void ApplyBtn_Click(object sender, RoutedEventArgs e)
    {
        if (RatingCombo.SelectedItem is System.Windows.Controls.ComboBoxItem item
            && item.Tag is string tag && int.TryParse(tag, out var r) && r >= 0)
            _vm.NewRating = r;

        _vm.SetFavorite = FavoriteCheck.IsChecked == true;
        _vm.SetHidden   = HiddenCheck.IsChecked   == true;

        var checkedTags    = _vm.TagChoices.Where(c => c.IsChecked).Select(c => c.Name);
        var checkedPeople  = _vm.PersonChoices.Where(c => c.IsChecked).Select(c => c.Name);
        var checkedPlaces  = _vm.PlaceChoices.Where(c => c.IsChecked).Select(c => c.Name);
        RunLogger.Action("BulkEditDialog", "Apply clicked",
            $"Count={_vm.SelectionCount}  Rating={_vm.NewRating}  " +
            $"Favorite={_vm.SetFavorite}  Hidden={_vm.SetHidden}  " +
            $"Tags=[{string.Join(",", checkedTags)}]  " +
            $"People=[{string.Join(",", checkedPeople)}]  " +
            $"Places=[{string.Join(",", checkedPlaces)}]");

        await _vm.ApplyCommand.ExecuteAsync(null);
        RunLogger.Info("BulkEditDialog", "Apply complete — dialog closing");
        DialogResult = true;
    }

    private async void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        RunLogger.Action("BulkEditDialog", "Delete clicked (confirmation pending)",
            $"Count={_vm.SelectionCount}");
        var confirm = MessageBox.Show(
            $"Move {_vm.SelectionCount} photos to trash?",
            "Confirm Delete", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK)
        {
            RunLogger.Info("BulkEditDialog", "Bulk delete cancelled by user");
            return;
        }
        RunLogger.Action("BulkEditDialog", "Bulk delete confirmed",
            $"Count={_vm.SelectionCount}");
        await _vm.BulkDeleteCommand.ExecuteAsync(null);
        RunLogger.Info("BulkEditDialog", "Bulk delete complete");
        DialogResult = true;
    }

    private async void NewTagBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var name = NewTagBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        RunLogger.Action("BulkEditDialog", "New tag created inline", $"Name=\"{name}\"");
        await _vm.CreateTagAsync(name);
        NewTagBox.Clear();
        e.Handled = true;
    }

    private async void NewPersonBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var name = NewPersonBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        RunLogger.Action("BulkEditDialog", "New person created inline", $"Name=\"{name}\"");
        await _vm.CreatePersonAsync(name);
        NewPersonBox.Clear();
        e.Handled = true;
    }

    private async void NewPlaceBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var name = NewPlaceBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        RunLogger.Action("BulkEditDialog", "New place created inline", $"Name=\"{name}\"");
        await _vm.CreatePlaceAsync(name);
        NewPlaceBox.Clear();
        e.Handled = true;
    }
}
