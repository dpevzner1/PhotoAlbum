using System.Windows;

namespace PhotoAlbum.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    public void ShowHeicWarning() => Shell.ShowHeicWarning();
}
