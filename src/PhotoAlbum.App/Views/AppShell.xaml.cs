using Microsoft.Extensions.DependencyInjection;
using PhotoAlbum.App.Services;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace PhotoAlbum.App.Views;

public partial class AppShell : UserControl
{
    private bool _navExpanded = true;
    private bool _navigating;
    private DeviceWatcher? _deviceWatcher;

    public AppShell()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Navigate("Library");
            WireDeviceWatcher();
        };
    }

    private void WireDeviceWatcher()
    {
        if (_deviceWatcher is not null) return;
        if (Application.Current is not App app || app.Services is not { } sp) return;

        _deviceWatcher = sp.GetService<DeviceWatcher>();
        if (_deviceWatcher is null) return;

        _deviceWatcher.DevicesChanged += devices =>
        {
            var connected = devices.Count > 0;
            PhoneBtn.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
            // If the user is on the Phone page and the phone goes away, bounce to Library.
            if (!connected && PhoneBtn.IsChecked == true)
                Navigate("Library");
        };

        if (Window.GetWindow(this) is { } window)
            _deviceWatcher.Attach(window);
    }

    public void ShowHeicWarning() => HeicWarningBanner.Visibility = Visibility.Visible;

    private void HamburgerBtn_Click(object sender, RoutedEventArgs e)
    {
        _navExpanded = !_navExpanded;
        var col = NavColumn;
        col.Width = _navExpanded
            ? new GridLength(220)
            : new GridLength(64);

        var labelVis = _navExpanded ? Visibility.Visible : Visibility.Collapsed;
        AppNameText.Visibility = labelVis;
        LibraryLabel.Visibility = labelVis;
        AlbumsLabel.Visibility = labelVis;
        PeopleLabel.Visibility = labelVis;
        PlacesLabel.Visibility = labelVis;
        EventsLabel.Visibility = labelVis;
        CloneLabel.Visibility = labelVis;
        TrashLabel.Visibility = labelVis;
        HiddenLabel.Visibility = labelVis;
        PhoneLabel.Visibility = labelVis;
        SettingsLabel.Visibility = labelVis;
    }

    // Called when a RadioButton becomes checked (user clicked it).
    // IMPORTANT: the IsChecked="True" attribute on LibraryBtn fires this during
    // XAML parse, before sibling buttons exist — guard with IsLoaded so we don't
    // call Navigate() (which touches all nav buttons) until the tree is built.
    private void NavBtn_Checked(object sender, RoutedEventArgs e)
    {
        if (_navigating || !IsLoaded) return;
        if (sender is RadioButton btn && btn.Tag is string tag)
            Navigate(tag);
    }

    // Settings is still a plain Button (not in the radio group)
    private void NavBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
            Navigate(tag);
    }

    private void Navigate(string destination)
    {
        _navigating = true;
        try
        {
            // Sync radio button selection without re-triggering navigation
            var navButtons = new RadioButton[] { LibraryBtn, AlbumsBtn, PeopleBtn, PlacesBtn,
                EventsBtn, CloneBtn, TrashBtn, HiddenBtn, PhoneBtn };
            foreach (var b in navButtons)
                b.IsChecked = b.Tag as string == destination;
        }
        finally { _navigating = false; }

        Page? page = destination switch
        {
            "Library"  => new LibraryView(),
            "Albums"   => new AlbumsView(),
            "People"   => new PeopleView(),
            "Places"   => new PlacesView(),
            "Events"   => new EventsView(),
            "Clone"    => new CloneView(),
            "Trash"    => new TrashView(),
            "Hidden"   => new HiddenView(),
            "Phone"    => new PhoneView(),
            "Settings" => new SettingsView(),
            _          => null,
        };
        if (page is not null)
            ContentFrame.Navigate(page);
    }

    private void HeicBannerClose_Click(object sender, RoutedEventArgs e)
        => HeicWarningBanner.Visibility = Visibility.Collapsed;

    private void HeicInstall_Click(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo(
            "ms-windows-store://pdp/?ProductId=9PMMSR1CGPWG") { UseShellExecute = true });
}
