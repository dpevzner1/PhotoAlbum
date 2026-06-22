using Microsoft.Extensions.DependencyInjection;
using PhotoAlbum.App.Services;
using PhotoAlbum.App.ViewModels;
using PhotoAlbum.Core.Domain;
using PhotoAlbum.Core.Interfaces;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Threading;

namespace PhotoAlbum.App.Views;

internal sealed record ExifEntry(string Label, string Value);

public partial class DetailView : Page
{
    private DetailViewModel? _vm;
    private HiddenContentService? _hidden;
    private IReadOnlyList<Tag> _allTags = [];
    private IReadOnlyList<Person> _allPeople = [];
    private IReadOnlyList<Place> _allPlaces = [];
    private IReadOnlyList<Album> _allAlbums = [];
    private double _zoom = 1.0;

    public static IReadOnlyList<(long Id, string Path)>? NavigationContext { get; set; }
    public static int NavigationIndex { get; set; }
    /// <summary>Page to return to when back is pressed. Null = navigate to new LibraryView.</summary>
    public static Page? OriginPage { get; set; }

    private long _mediaItemId;

    public DetailView(long mediaItemId, string filePath)
    {
        InitializeComponent();
        _mediaItemId = mediaItemId;

        if (System.Windows.Application.Current is App app && app.Services is { } sp)
        {
            _vm     = sp.GetRequiredService<DetailViewModel>();
            _hidden = sp.GetService<HiddenContentService>();
            DataContext = _vm;
            _ = _vm.LoadAsync(mediaItemId, filePath);
        }

        Loaded += async (_, _) =>
        {
            UpdateVideoPanel();
            UpdateNavCounter();
            ApplyZoom();
            Focusable = true;
            // Defer focus to after input queue drains so each new page captures key events
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => Keyboard.Focus(this));
            await LoadSidebarAsync();
        };
    }

    // ── Sidebar async loader ─────────────────────────────────────────────────

    private async Task LoadSidebarAsync()
    {
        if (Application.Current is not App app || app.Services is not { } sp) return;

        RunLogger.Info("DetailView", "LoadSidebarAsync started", $"MediaItemId={_mediaItemId}");

        var mediaRepo  = sp.GetRequiredService<IMediaItemRepository>();
        var tagRepo    = sp.GetRequiredService<ITagRepository>();
        var personRepo = sp.GetRequiredService<IPersonRepository>();
        var placeRepo  = sp.GetRequiredService<IPlaceRepository>();
        var albumRepo  = sp.GetRequiredService<IAlbumRepository>();

        // Load all available tags/people/places/albums for picker dropdowns
        _allTags   = await tagRepo.GetAllAsync();
        _allPeople = await personRepo.GetAllAsync();
        _allPlaces = await placeRepo.GetAllAsync();
        _allAlbums = await albumRepo.GetAllAsync();
        RunLogger.Info("DetailView", "Picker lists loaded",
            $"Tags={_allTags.Count}  People={_allPeople.Count}  Places={_allPlaces.Count}");

        // Rating + item metadata
        var item = await mediaRepo.GetByIdAsync(_mediaItemId);
        RefreshStarIcons(item?.Rating ?? 0);
        RefreshMetaOverlay(item);

        // GPS
        if (item?.Latitude is not null && item.Longitude is not null)
        {
            GpsCoordText.Text     = $"Lat {item.Latitude.Value:F5}°  Lon {item.Longitude.Value:F5}°";
            GpsSection.Visibility = Visibility.Visible;
            ShowOnMapBtn.Tag      = (item.Latitude.Value, item.Longitude.Value);
        }

        // File size
        var filePath = await mediaRepo.GetPrimaryFilePathAsync(_mediaItemId);
        if (filePath is not null && File.Exists(filePath))
        {
            var size = new FileInfo(filePath).Length;
            FileSizeText.Text = FormatBytes(size);
            await LoadExifAsync(filePath);
        }
    }

    private static string FormatBytes(long b) => b switch
    {
        >= 1_073_741_824 => $"{b / 1_073_741_824.0:F1} GB",
        >= 1_048_576     => $"{b / 1_048_576.0:F1} MB",
        >= 1_024         => $"{b / 1_024.0:F1} KB",
        _                => $"{b} B"
    };

    // ── EXIF reading ─────────────────────────────────────────────────────────

    private async Task LoadExifAsync(string filePath)
    {
        RunLogger.Info("DetailView", "LoadExifAsync started", filePath);
        try
        {
            var ext = Path.GetExtension(filePath).ToUpperInvariant();
            if (ext is not (".JPG" or ".JPEG" or ".TIFF" or ".TIF" or ".HEIC" or ".PNG"))
            {
                RunLogger.Info("DetailView", "LoadExifAsync skipped — unsupported extension", ext);
                return;
            }

            var entries = await Task.Run(() => ReadExif(filePath));
            RunLogger.Info("DetailView", "EXIF read complete", $"{entries.Count} field(s)");
            if (entries.Count == 0) return;

            ExifList.ItemsSource = entries;
            ExifSection.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            RunLogger.Warn("DetailView", "LoadExifAsync failed (non-critical)", ex);
        }
    }

    private static IReadOnlyList<ExifEntry> ReadExif(string filePath)
    {
        var list = new List<ExifEntry>();
        try
        {
            using var stream = File.OpenRead(filePath);
            var decoder = BitmapDecoder.Create(stream,
                BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (decoder.Frames[0].Metadata is not BitmapMetadata m) return list;

            Add(list, "Make",     m.CameraManufacturer);
            Add(list, "Model",    m.CameraModel);
            Add(list, "Date",     FormatExifDate(m.DateTaken));

            // Exposure
            var expRaw = m.GetQuery("/app1/ifd/exif/{ushort=33434}");
            if (expRaw is ulong expU)
            {
                var num = (uint)(expU >> 32); var den = (uint)(expU & 0xFFFFFFFF);
                if (den > 0) Add(list, "Exposure", den > num ? $"1/{den / num}s" : $"{(double)num / den:F1}s");
            }

            var fRaw = m.GetQuery("/app1/ifd/exif/{ushort=33437}");
            if (fRaw is ulong fU)
            {
                var num = (uint)(fU >> 32); var den = (uint)(fU & 0xFFFFFFFF);
                if (den > 0) Add(list, "Aperture", $"f/{(double)num / den:F1}");
            }

            var isoRaw = m.GetQuery("/app1/ifd/exif/{ushort=34855}");
            if (isoRaw is ushort iso) Add(list, "ISO", iso.ToString());
            else if (isoRaw is uint isoU) Add(list, "ISO", isoU.ToString());

            var flRaw = m.GetQuery("/app1/ifd/exif/{ushort=37386}");
            if (flRaw is ulong flU)
            {
                var num = (uint)(flU >> 32); var den = (uint)(flU & 0xFFFFFFFF);
                if (den > 0) Add(list, "Focal length", $"{(double)num / den:F0}mm");
            }

            var flashRaw = m.GetQuery("/app1/ifd/exif/{ushort=37385}");
            if (flashRaw is ushort flash) Add(list, "Flash", (flash & 1) == 1 ? "Fired" : "No flash");

            Add(list, "Software", m.GetQuery("/app1/ifd/{ushort=305}")?.ToString());
        }
        catch { /* swallow per-file errors */ }
        return list;
    }

    private static void Add(List<ExifEntry> list, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) list.Add(new ExifEntry(label, value));
    }

    private static string? FormatExifDate(string? raw)
    {
        if (raw is null) return null;
        if (DateTime.TryParseExact(raw, "yyyy:MM:dd HH:mm:ss", null,
            System.Globalization.DateTimeStyles.None, out var dt))
            return dt.ToString("MMMM d, yyyy  HH:mm");
        return raw;
    }

    // ── Tag picker ───────────────────────────────────────────────────────────

    private void AddTagBtn_Click(object sender, RoutedEventArgs e)
    {
        RunLogger.Action("DetailView", "Add Tag button clicked", $"MediaItemId={_mediaItemId}");
        PopulateTagPicker(_allTags);
        TagPickerPopup.IsOpen = true;
        TagSearchBox.Clear();
        TagSearchBox.Focus();
    }

    private void PopulateTagPicker(IEnumerable<Tag> tags)
    {
        var applied = _vm?.Tags.Select(t => t.Id).ToHashSet() ?? [];
        TagPickerList.ItemsSource = tags.Where(t => !applied.Contains(t.Id)).ToList();
    }

    private void TagSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = TagSearchBox.Text.Trim();
        var filtered = string.IsNullOrEmpty(q)
            ? _allTags
            : _allTags.Where(t => t.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
        PopulateTagPicker(filtered);
        TagHintText.Visibility = !string.IsNullOrWhiteSpace(q) ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void TagSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var name = TagSearchBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        if (Application.Current is not App app || app.Services is not { } sp) return;

        RunLogger.Action("DetailView", "Tag search Enter pressed — create/apply tag",
            $"MediaItemId={_mediaItemId}  TagName=\"{name}\"");

        var tagRepo = sp.GetRequiredService<ITagRepository>();
        var existing = _allTags.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var tagId = existing?.Id ?? await tagRepo.InsertAsync(new Tag { Name = name });
        if (existing is null) RunLogger.Info("DetailView", "New tag created", $"Name=\"{name}\"  Id={tagId}");
        await tagRepo.TagMediaAsync(_mediaItemId, tagId);
        if (existing is null) _allTags = await tagRepo.GetAllAsync();

        await RefreshTagsAsync(tagRepo);
        RunLogger.Info("DetailView", "Tag applied", $"TagId={tagId}  MediaItemId={_mediaItemId}");
        TagPickerPopup.IsOpen = false;
        e.Handled = true;
    }

    private async void TagPickerItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not long tagId) return;
        if (Application.Current is not App app || app.Services is not { } sp) return;

        RunLogger.Action("DetailView", "Tag picker item clicked",
            $"MediaItemId={_mediaItemId}  TagId={tagId}");

        var tagRepo = sp.GetRequiredService<ITagRepository>();
        await tagRepo.TagMediaAsync(_mediaItemId, tagId);
        await RefreshTagsAsync(tagRepo);
        RunLogger.Info("DetailView", "Tag applied via picker", $"TagId={tagId}");
        TagPickerPopup.IsOpen = false;
    }

    private async void RemoveTagBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not long tagId) return;
        if (Application.Current is not App app || app.Services is not { } sp) return;

        RunLogger.Action("DetailView", "Remove tag button clicked",
            $"MediaItemId={_mediaItemId}  TagId={tagId}");

        var tagRepo = sp.GetRequiredService<ITagRepository>();
        await tagRepo.UntagMediaAsync(_mediaItemId, tagId);
        await RefreshTagsAsync(tagRepo);
        RunLogger.Info("DetailView", "Tag removed", $"TagId={tagId}");
    }

    private async Task RefreshTagsAsync(ITagRepository tagRepo)
    {
        if (_vm is null) return;
        _vm.Tags = await tagRepo.GetTagsForMediaAsync(_mediaItemId);
    }

    // ── Person picker ────────────────────────────────────────────────────────

    private void AddPersonBtn_Click(object sender, RoutedEventArgs e)
    {
        RunLogger.Action("DetailView", "Add Person button clicked", $"MediaItemId={_mediaItemId}");
        PopulatePersonPicker(_allPeople);
        PersonPickerPopup.IsOpen = true;
        PersonSearchBox.Clear();
        PersonSearchBox.Focus();
    }

    private void PopulatePersonPicker(IEnumerable<Person> people)
    {
        var applied = _vm?.People.Select(p => p.Id).ToHashSet() ?? [];
        PersonPickerList.ItemsSource = people.Where(p => !applied.Contains(p.Id)).ToList();
    }

    private void PersonSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = PersonSearchBox.Text.Trim();
        var filtered = string.IsNullOrEmpty(q)
            ? _allPeople
            : _allPeople.Where(p => p.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
        PopulatePersonPicker(filtered);
        PersonHintText.Visibility = !string.IsNullOrWhiteSpace(q) ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void PersonSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var name = PersonSearchBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        if (Application.Current is not App app || app.Services is not { } sp) return;

        RunLogger.Action("DetailView", "Person search Enter pressed — create/apply person",
            $"MediaItemId={_mediaItemId}  PersonName=\"{name}\"");

        var personRepo = sp.GetRequiredService<IPersonRepository>();
        var existing = _allPeople.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var personId = existing?.Id ?? await personRepo.InsertAsync(new Person { Name = name });
        if (existing is null) RunLogger.Info("DetailView", "New person created", $"Name=\"{name}\"  Id={personId}");
        await personRepo.TagMediaAsync(_mediaItemId, personId);
        if (existing is null) _allPeople = await personRepo.GetAllAsync();

        await RefreshPeopleAsync(personRepo);
        RunLogger.Info("DetailView", "Person tagged", $"PersonId={personId}  MediaItemId={_mediaItemId}");
        PersonPickerPopup.IsOpen = false;
        e.Handled = true;
    }

    private async void PersonPickerItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not long personId) return;
        if (Application.Current is not App app || app.Services is not { } sp) return;

        RunLogger.Action("DetailView", "Person picker item clicked",
            $"MediaItemId={_mediaItemId}  PersonId={personId}");

        var personRepo = sp.GetRequiredService<IPersonRepository>();
        await personRepo.TagMediaAsync(_mediaItemId, personId);
        await RefreshPeopleAsync(personRepo);
        RunLogger.Info("DetailView", "Person tagged via picker", $"PersonId={personId}");
        PersonPickerPopup.IsOpen = false;
    }

    private async void RemovePersonBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not long personId) return;
        if (Application.Current is not App app || app.Services is not { } sp) return;

        RunLogger.Action("DetailView", "Remove person button clicked",
            $"MediaItemId={_mediaItemId}  PersonId={personId}");

        var personRepo = sp.GetRequiredService<IPersonRepository>();
        await personRepo.UntagMediaAsync(_mediaItemId, personId);
        await RefreshPeopleAsync(personRepo);
        RunLogger.Info("DetailView", "Person untagged", $"PersonId={personId}");
    }

    private async Task RefreshPeopleAsync(IPersonRepository personRepo)
    {
        if (_vm is null) return;
        _vm.People = await personRepo.GetPeopleForMediaAsync(_mediaItemId);
    }

    // ── Place picker ─────────────────────────────────────────────────────────

    private void AddPlaceBtn_Click(object sender, RoutedEventArgs e)
    {
        PopulatePlacePicker(_allPlaces);
        PlacePickerPopup.IsOpen = true;
        PlaceSearchBox.Clear();
        PlaceSearchBox.Focus();
    }

    private void PopulatePlacePicker(IEnumerable<Place> places)
    {
        var applied = _vm?.Places.Select(p => p.Id).ToHashSet() ?? [];
        PlacePickerList.ItemsSource = places.Where(p => !applied.Contains(p.Id)).ToList();
    }

    private void PlaceSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = PlaceSearchBox.Text.Trim();
        var filtered = string.IsNullOrEmpty(q)
            ? _allPlaces
            : _allPlaces.Where(p => p.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
        PopulatePlacePicker(filtered);
    }

    private async void PlacePickerItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not long placeId) return;
        if (Application.Current is not App app || app.Services is not { } sp) return;
        var placeRepo = sp.GetRequiredService<IPlaceRepository>();
        await placeRepo.AssignMediaAsync(_mediaItemId, placeId);
        await RefreshPlacesAsync(placeRepo);
        RunLogger.Info("DetailView", "Place assigned via picker", $"PlaceId={placeId}");
        PlacePickerPopup.IsOpen = false;
        RefreshMetaOverlay(_vm?.Item);
    }

    private async void RemovePlaceBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not long placeId) return;
        if (Application.Current is not App app || app.Services is not { } sp) return;
        var placeRepo = sp.GetRequiredService<IPlaceRepository>();
        await placeRepo.UnassignMediaAsync(_mediaItemId, placeId);
        await RefreshPlacesAsync(placeRepo);
        RunLogger.Info("DetailView", "Place removed", $"PlaceId={placeId}");
        RefreshMetaOverlay(_vm?.Item);
    }

    private async Task RefreshPlacesAsync(IPlaceRepository placeRepo)
    {
        if (_vm is null) return;
        _vm.Places = await placeRepo.GetPlacesForMediaAsync(_mediaItemId);
        RefreshOverlayContextual();
    }

    // ── Album picker ─────────────────────────────────────────────────────────

    private void AddToAlbumBtn_Click(object sender, RoutedEventArgs e)
    {
        RunLogger.Action("DetailView", "Add to Album button clicked", $"MediaItemId={_mediaItemId}");
        PopulateAlbumPicker(_allAlbums);
        AlbumPickerPopup.IsOpen = true;
        AlbumSearchBox.Clear();
        AlbumSearchBox.Focus();
    }

    private void PopulateAlbumPicker(IEnumerable<Album> albums)
    {
        var applied = _vm?.Albums.Select(a => a.Id).ToHashSet() ?? [];
        AlbumPickerList.ItemsSource = albums.Where(a => !applied.Contains(a.Id)).ToList();
    }

    private void AlbumSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = AlbumSearchBox.Text.Trim();
        var filtered = string.IsNullOrEmpty(q)
            ? _allAlbums
            : _allAlbums.Where(a => a.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
        PopulateAlbumPicker(filtered);
    }

    private async void AlbumPickerItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not long albumId) return;
        if (Application.Current is not App app || app.Services is not { } sp) return;
        RunLogger.Action("DetailView", "Album picker item clicked",
            $"MediaItemId={_mediaItemId}  AlbumId={albumId}");
        var albumRepo = sp.GetRequiredService<IAlbumRepository>();
        await albumRepo.AddMediaAsync(albumId, _mediaItemId);
        await RefreshAlbumsAsync(albumRepo);
        RunLogger.Info("DetailView", "Photo added to album", $"AlbumId={albumId}");
        AlbumPickerPopup.IsOpen = false;
    }

    private async void RemoveFromAlbumBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not long albumId) return;
        if (Application.Current is not App app || app.Services is not { } sp) return;
        RunLogger.Action("DetailView", "Remove from album clicked",
            $"MediaItemId={_mediaItemId}  AlbumId={albumId}");

        // Gate: removing from a hidden album requires PIN if not already unlocked
        if (_hidden?.IsAlbumHidden(albumId) == true && _hidden?.IsUnlocked == false)
        {
            if (_hidden.HasPin)
            {
                var pinDlg = new InputDialog("Enter PIN to modify hidden album:", "Hidden Album")
                    { Owner = Window.GetWindow(this) };
                if (pinDlg.ShowDialog() != true || string.IsNullOrEmpty(pinDlg.Result)) return;
                if (!await _hidden.TryUnlockWithPinAsync(pinDlg.Result))
                {
                    MessageBox.Show("Incorrect PIN.", "Access Denied",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
            else
            {
                _hidden.TryUnlockNoPin();
            }
        }

        var albumRepo = sp.GetRequiredService<IAlbumRepository>();
        await albumRepo.RemoveMediaAsync(albumId, _mediaItemId);
        await RefreshAlbumsAsync(albumRepo);
        RunLogger.Info("DetailView", "Photo removed from album", $"AlbumId={albumId}");
    }

    private async Task RefreshAlbumsAsync(IAlbumRepository albumRepo)
    {
        if (_vm is null) return;
        _vm.Albums = await albumRepo.GetAlbumsForMediaAsync(_mediaItemId);
        RefreshOverlayContextual();
    }

    // ── Navigation ───────────────────────────────────────────────────────────

    private void UpdateVideoPanel()
    {
        if (_vm?.Item?.MediaType == MediaType.Video)
        {
            FullImage.Visibility  = Visibility.Collapsed;
            VideoPanel.Visibility = Visibility.Visible;
        }
    }

    // ── Zoom / pan ───────────────────────────────────────────────────────────

    private void ImageViewport_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyZoom();

    private void ImageScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!(Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))) return;
        e.Handled = true;
        _zoom = Math.Clamp(_zoom + (e.Delta > 0 ? 0.15 : -0.15), 0.25, 8.0);
        if (_zoom < 1.05) _zoom = 1.0;
        ApplyZoom();
    }

    private void ApplyZoom()
    {
        var vw = ImageViewport?.ActualWidth ?? 0;
        var vh = ImageViewport?.ActualHeight ?? 0;
        if (vw <= 0 || vh <= 0) return;
        FullImage.Width  = vw * _zoom;
        FullImage.Height = vh * _zoom;
    }

    private void ResetZoom()
    {
        _zoom = 1.0;
        ApplyZoom();
    }

    // ── Navigation ───────────────────────────────────────────────────────────

    private void BackBtn_Click(object sender, RoutedEventArgs e)
    {
        RunLogger.Action("DetailView", "Back button clicked", $"MediaItemId={_mediaItemId}");
        NavigateBack();
    }

    private void NavigateBack()
    {
        var ns = NavigationService;
        if (ns is null) return;
        var target = OriginPage ?? (Page)new LibraryView();
        ns.Navigate(target);
        ns.Navigated += ClearDetailBackStack;
    }

    private void NavigateToLibrary() => NavigateBack();

    private void ClearDetailBackStack(object sender, NavigationEventArgs e)
    {
        var ns = NavigationService;
        if (ns is null) return;
        ns.Navigated -= ClearDetailBackStack;
        while (ns.CanGoBack)
            ns.RemoveBackEntry();
    }

    private void PrevBtn_Click(object sender, RoutedEventArgs e)
    {
        RunLogger.Action("DetailView", "Prev button clicked",
            $"CurrentIndex={NavigationIndex}  MediaItemId={_mediaItemId}");
        NavigateRelative(-1);
    }

    private void NextBtn_Click(object sender, RoutedEventArgs e)
    {
        RunLogger.Action("DetailView", "Next button clicked",
            $"CurrentIndex={NavigationIndex}  MediaItemId={_mediaItemId}");
        NavigateRelative(1);
    }

    private void NavigateRelative(int delta)
    {
        var ctx = NavigationContext;
        if (ctx is null) return;
        var idx = NavigationIndex + delta;
        if (idx < 0 || idx >= ctx.Count) return;
        NavigationIndex = idx;
        ResetZoom();
        RunLogger.Action("DetailView", $"Navigate {(delta < 0 ? "Prev" : "Next")}",
            $"NewIndex={idx}  MediaItemId={ctx[idx].Id}");
        NavigationService?.Navigate(new DetailView(ctx[idx].Id, ctx[idx].Path));
    }

    private void UpdateNavCounter()
    {
        var ctx = NavigationContext;
        if (NavCounterText is null || ctx is null) return;
        NavCounterText.Text = $"{NavigationIndex + 1} / {ctx.Count}";
        if (PrevBtn is not null) PrevBtn.IsEnabled = NavigationIndex > 0;
        if (NextBtn is not null) NextBtn.IsEnabled = NavigationIndex < ctx.Count - 1;
    }

    private void Page_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        switch (e.Key)
        {
            case Key.Left:
                RunLogger.Action("DetailView", "KeyDown ← (navigate prev)",
                    $"CurrentIndex={NavigationIndex}");
                NavigateRelative(-1); e.Handled = true; break;
            case Key.Right:
                RunLogger.Action("DetailView", "KeyDown → (navigate next)",
                    $"CurrentIndex={NavigationIndex}");
                NavigateRelative(1); e.Handled = true; break;
            case Key.Back:
            case Key.Escape:
                RunLogger.Action("DetailView", $"KeyDown {e.Key} (go back to library)",
                    $"MediaItemId={_mediaItemId}");
                NavigateToLibrary(); e.Handled = true; break;
        }
    }

    // ── Other handlers ───────────────────────────────────────────────────────

    private async void StarBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (sender is Button btn && btn.Tag is string tag && int.TryParse(tag, out var rating))
        {
            RunLogger.Action("DetailView", "Star rating set",
                $"MediaItemId={_mediaItemId}  Rating={rating}");
            await _vm.SetRatingCommand.ExecuteAsync(rating);
            RefreshStarIcons(rating);
            RunLogger.Info("DetailView", "Star rating visual refreshed", $"Rating={rating}");
        }
    }

    private void RefreshStarIcons(int rating)
    {
        var filled = TryFindResource("AccentBrush") as Brush
                     ?? new SolidColorBrush(Color.FromRgb(0, 120, 215));
        var empty  = new SolidColorBrush(Color.FromArgb(100, 120, 120, 120));
        var stars  = new[] { Star1Text, Star2Text, Star3Text, Star4Text, Star5Text };
        for (int i = 0; i < stars.Length; i++)
        {
            stars[i].Text       = i < rating ? "★" : "☆";
            stars[i].Foreground = i < rating ? filled : empty;
        }
        // Mirror in the overlay
        if (OverlayRatingText is not null)
        {
            OverlayRatingText.Text = string.Concat(
                Enumerable.Range(0, 5).Select(i => i < rating ? "★" : "☆"));
        }
    }

    private void RefreshMetaOverlay(MediaItem? item)
    {
        if (item is null) return;

        if (item.CaptureUtc is { } d)
        {
            OverlayDateText.Text       = d.ToLocalTime().ToString("MMM d, yyyy");
            OverlayDateText.Visibility = Visibility.Visible;
        }

        // Place + albums from VM — defer until after sidebar loads
        Dispatcher.BeginInvoke(DispatcherPriority.Background, RefreshOverlayContextual);
    }

    private void RefreshOverlayContextual()
    {
        // Places
        var places = _vm?.Places;
        if (places is { Count: > 0 })
        {
            OverlayPlaceText.Text        = string.Join(" · ", places.Select(p => p.Name));
            OverlayPlacePanel.Visibility = Visibility.Visible;
        }
        else
        {
            OverlayPlacePanel.Visibility = Visibility.Collapsed;
        }

        // Albums
        OverlayAlbumsControl.ItemsSource = _vm?.Albums;
    }

    private async void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        RunLogger.Action("DetailView", "Delete button clicked (confirmation pending)",
            $"MediaItemId={_mediaItemId}");
        var result = MessageBox.Show(
            "Move this photo to trash?", "Move to Trash",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK)
        {
            RunLogger.Info("DetailView", "Delete cancelled by user");
            return;
        }
        RunLogger.Action("DetailView", "Delete confirmed — soft delete to Trash",
            $"MediaItemId={_mediaItemId}");
        if (_vm is not null)
        {
            await _vm.SoftDeleteCommand.ExecuteAsync(null);
            RunLogger.Info("DetailView", "Soft delete complete — navigating back");
            NavigationService?.GoBack();
        }
    }

    private void OpenSystemPlayer_Click(object sender, RoutedEventArgs e)
    {
        if (_vm?.CurrentFilePath is { } path)
        {
            RunLogger.Action("DetailView", "Open in system player", path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
    }

    private void NotesBox_LostFocus(object sender, RoutedEventArgs e) { /* binding handles it */ }

    private void ShowOnMapBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ShowOnMapBtn.Tag is not (double lat, double lon)) return;
        RunLogger.Action("DetailView", "Show on Map clicked",
            $"MediaItemId={_mediaItemId}  Lat={lat:F6}  Lon={lon:F6}");
        Process.Start(new ProcessStartInfo(
            $"https://www.google.com/maps/search/?api=1&query={lat:F6},{lon:F6}")
            { UseShellExecute = true });
    }
}
