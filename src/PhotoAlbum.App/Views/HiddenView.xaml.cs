using Microsoft.Extensions.DependencyInjection;
using PhotoAlbum.App.Services;
using PhotoAlbum.App.ViewModels;
using PhotoAlbum.Core.Domain;
using PhotoAlbum.Core.Interfaces;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace PhotoAlbum.App.Views;

public partial class HiddenView : Page
{
    private HiddenContentService? _hidden;
    private IAlbumRepository?     _albumRepo;
    private ITagRepository?       _tagRepo;
    private IMediaItemRepository? _mediaRepo;

    private List<MediaItemVm> _vms = [];

    public HiddenView()
    {
        InitializeComponent();

        if (Application.Current is App app && app.Services is { } sp)
        {
            _hidden    = sp.GetRequiredService<HiddenContentService>();
            _albumRepo = sp.GetRequiredService<IAlbumRepository>();
            _tagRepo   = sp.GetRequiredService<ITagRepository>();
            _mediaRepo = sp.GetRequiredService<IMediaItemRepository>();

            _hidden.LockStateChanged += () => Dispatcher.Invoke(RefreshLockState);
            _hidden.HiddenSetChanged += () => Dispatcher.Invoke(() => _ = RefreshContentAsync());
        }

        RefreshLockState();
    }

    // ── Lock state ────────────────────────────────────────────────────────────

    private void RefreshLockState()
    {
        bool unlocked = _hidden?.IsUnlocked == true;

        LockedPanel.Visibility   = unlocked ? Visibility.Collapsed : Visibility.Visible;
        UnlockedPanel.Visibility = unlocked ? Visibility.Visible   : Visibility.Collapsed;

        // Header buttons
        LockBtn.Visibility      = unlocked ? Visibility.Visible : Visibility.Collapsed;
        SetPinBtn.Visibility    = (unlocked && _hidden?.HasPin == false) ? Visibility.Visible : Visibility.Collapsed;
        ChangePinBtn.Visibility = (unlocked && _hidden?.HasPin == true)  ? Visibility.Visible : Visibility.Collapsed;
        RemovePinBtn.Visibility = (unlocked && _hidden?.HasPin == true)  ? Visibility.Visible : Visibility.Collapsed;

        if (!unlocked)
        {
            bool hasPin = _hidden?.HasPin == true;
            PinEntryPanel.Visibility = hasPin ? Visibility.Visible : Visibility.Collapsed;
            LockSubText.Text = hasPin
                ? "Enter your PIN to view hidden content."
                : "Click Unlock to access hidden photos.";
            PinBox.Clear();
            PinErrorText.Visibility = Visibility.Collapsed;
        }
        else
        {
            _ = RefreshContentAsync();
        }
    }

    private async void UnlockBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_hidden is null) return;

        if (_hidden.HasPin)
        {
            var pin = PinBox.Password;
            if (string.IsNullOrEmpty(pin))
            {
                PinErrorText.Text = "Enter your PIN.";
                PinErrorText.Visibility = Visibility.Visible;
                return;
            }
            bool ok = await _hidden.TryUnlockWithPinAsync(pin);
            if (!ok)
            {
                PinErrorText.Text = "Incorrect PIN.";
                PinErrorText.Visibility = Visibility.Visible;
                PinBox.Clear();
                return;
            }
        }
        else
        {
            _hidden.TryUnlockNoPin();
        }
    }

    private void PinBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) UnlockBtn_Click(sender, e);
    }

    private void LockBtn_Click(object sender, RoutedEventArgs e)
        => _hidden?.Lock();

    // ── PIN management ────────────────────────────────────────────────────────

    private async void SetPinBtn_Click(object sender, RoutedEventArgs e)
        => await PromptSetPinAsync("Set PIN", "Enter a new PIN:");

    private async void ChangePinBtn_Click(object sender, RoutedEventArgs e)
        => await PromptSetPinAsync("Change PIN", "Enter a new PIN:");

    private async void RemovePinBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_hidden is null) return;
        var result = MessageBox.Show(
            "Remove the PIN? Hidden content will only require one click to unlock.",
            "Remove PIN", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (result != MessageBoxResult.OK) return;
        await _hidden.ClearPinAsync();
        RefreshLockState();
    }

    private async Task PromptSetPinAsync(string title, string prompt)
    {
        if (_hidden is null) return;
        var dlg = new InputDialog(prompt, title) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || string.IsNullOrEmpty(dlg.Result)) return;
        // Confirm
        var confirm = new InputDialog("Confirm PIN:", title) { Owner = Window.GetWindow(this) };
        if (confirm.ShowDialog() != true || confirm.Result != dlg.Result)
        {
            MessageBox.Show("PINs did not match.", title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        await _hidden.SetPinAsync(dlg.Result);
        RefreshLockState();
        RunLogger.Info("HiddenView", "PIN updated");
    }

    // ── Content (unlocked) ────────────────────────────────────────────────────

    private async Task RefreshContentAsync()
    {
        if (_hidden is null || _albumRepo is null || _tagRepo is null || _mediaRepo is null) return;

        // Hidden albums list
        var allAlbums = await _albumRepo.GetAllAsync();
        var hiddenAlbums = allAlbums.Where(a => _hidden.IsAlbumHidden(a.Id)).ToList();
        var visibleAlbums = allAlbums.Where(a => !_hidden.IsAlbumHidden(a.Id)).ToList();

        HiddenAlbumsList.ItemsSource = hiddenAlbums;
        NoHiddenAlbumsText.Visibility = hiddenAlbums.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AddAlbumCombo.ItemsSource = visibleAlbums;

        // Hidden tags list
        var allTags = await _tagRepo.GetAllAsync();
        var hiddenTags = allTags.Where(t => _hidden.IsTagHidden(t.Id)).ToList();
        var visibleTags = allTags.Where(t => !_hidden.IsTagHidden(t.Id)).ToList();

        HiddenTagsList.ItemsSource = hiddenTags;
        NoHiddenTagsText.Visibility = hiddenTags.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AddTagCombo.ItemsSource = visibleTags;

        // Media grid
        await RefreshMediaGridAsync();
    }

    private async Task RefreshMediaGridAsync()
    {
        if (_mediaRepo is null || _hidden is null) return;

        var excludedIds = _hidden.ExcludedMediaIds;
        if (excludedIds.Count == 0)
        {
            _vms = [];
            HiddenMediaGrid.ItemsSource = null;
            HiddenEmptyPanel.Visibility = Visibility.Visible;
            return;
        }

        HiddenEmptyPanel.Visibility = Visibility.Collapsed;

        // Load all individually hidden + excluded-by-category items
        // For individually-hidden (IsHidden=true):
        var hiddenItems = await _mediaRepo.QueryAsync(
            new Core.Interfaces.MediaFilter(IsHidden: true, PageSize: 100_000));

        // Combine: individually hidden + album/tag excluded
        var allHiddenIds = new HashSet<long>(excludedIds);
        allHiddenIds.UnionWith(hiddenItems.Select(i => i.Id));

        // Load MediaItem records for excluded set (not already loaded)
        var alreadyLoaded = new HashSet<long>(hiddenItems.Select(i => i.Id));
        var toLoad = allHiddenIds.Where(id => !alreadyLoaded.Contains(id)).ToList();

        var combined = new List<Core.Domain.MediaItem>(hiddenItems);
        foreach (var id in toLoad)
        {
            var item = await _mediaRepo.GetByIdAsync(id);
            if (item is not null) combined.Add(item);
        }

        _vms = combined.Select(m => new MediaItemVm(m)).ToList();
        foreach (var vm in _vms) vm.LoadThumbnail();
        HiddenMediaGrid.ItemsSource = _vms;

        HiddenEmptyPanel.Visibility = _vms.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Add / remove hidden albums ────────────────────────────────────────────

    private async void AddHiddenAlbumBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_hidden is null || AddAlbumCombo.SelectedItem is not Album album) return;
        RunLogger.Action("HiddenView", "Hide album", $"AlbumId={album.Id} Name={album.Name}");
        await _hidden.HideAlbumAsync(album.Id);
        AddAlbumCombo.SelectedItem = null;
    }

    private async void UnhideAlbumBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_hidden is null || sender is not Button { Tag: long albumId }) return;
        RunLogger.Action("HiddenView", "Unhide album", $"AlbumId={albumId}");
        await _hidden.UnhideAlbumAsync(albumId);
    }

    // ── Add / remove hidden tags ──────────────────────────────────────────────

    private async void AddHiddenTagBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_hidden is null || AddTagCombo.SelectedItem is not Tag tag) return;
        RunLogger.Action("HiddenView", "Hide tag", $"TagId={tag.Id} Name={tag.Name}");
        await _hidden.HideTagAsync(tag.Id);
        AddTagCombo.SelectedItem = null;
    }

    private async void UnhideTagBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_hidden is null || sender is not Button { Tag: long tagId }) return;
        RunLogger.Action("HiddenView", "Unhide tag", $"TagId={tagId}");
        await _hidden.UnhideTagAsync(tagId);
    }

    // ── Media click ───────────────────────────────────────────────────────────

    private void HiddenThumb_Click(object sender, MouseButtonEventArgs e)
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
