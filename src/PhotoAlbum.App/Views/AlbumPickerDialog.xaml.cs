using PhotoAlbum.Core.Domain;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PhotoAlbum.App.Views;

public partial class AlbumPickerDialog : Window
{
    public long? SelectedAlbumId { get; private set; }

    public AlbumPickerDialog(IReadOnlyList<Album> albums)
    {
        InitializeComponent();
        AlbumList.ItemsSource = albums;
    }

    private void AddBtn_Click(object sender, RoutedEventArgs e)
    {
        if (AlbumList.SelectedItem is Album a)
        {
            SelectedAlbumId = a.Id;
            DialogResult = true;
        }
        else
        {
            MessageBox.Show("Please select an album.", "No Selection",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void AlbumList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (AlbumList.SelectedItem is Album) AddBtn_Click(sender, e);
    }
}
