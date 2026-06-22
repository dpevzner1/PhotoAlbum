using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoAlbum.App.Services;
using PhotoAlbum.App.Views;
using PhotoAlbum.Core.Domain;
using PhotoAlbum.Core.Interfaces;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace PhotoAlbum.App.ViewModels;

public sealed partial class MediaItemVm : ObservableObject
{
    public long Id { get; }
    public string OriginalName { get; }
    public MediaType MediaType { get; }
    public string? ThumbnailPath { get; }
    public int Rating { get; }
    public bool IsFavorite { get; }
    public int RotationDegrees { get; }
    [ObservableProperty] private bool _isSelected;
    public bool IsHiddenContent { get; set; }
    public DateTime? CaptureUtc { get; }

    [ObservableProperty] private BitmapImage? _thumbnail;

    public MediaItemVm(MediaItem m)
    {
        Id = m.Id;
        OriginalName = m.OriginalName;
        MediaType = m.MediaType;
        ThumbnailPath = m.ThumbnailPath;
        Rating = m.Rating;
        IsFavorite = m.IsFavorite;
        RotationDegrees = m.RotationDegrees;
        CaptureUtc = m.CaptureUtc;
    }

    public void LoadThumbnail()
    {
        if (ThumbnailPath is null || !File.Exists(ThumbnailPath)) return;
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(ThumbnailPath);
        bmp.DecodePixelWidth = 300;
        bmp.EndInit();
        bmp.Freeze();
        Thumbnail = bmp;
    }
}

public sealed partial class LibraryViewModel : ObservableObject
{
    private readonly IMediaItemRepository _mediaRepo;
    private readonly IndexOrchestrator _indexer;
    private readonly TagService _tagService;
    private readonly IPersonRepository _personRepo;
    private readonly IPlaceRepository _placeRepo;
    private readonly IEventRepository _eventRepo;
    private readonly IAlbumRepository _albumRepo;
    private readonly HiddenContentService? _hidden;

    [ObservableProperty] private ObservableCollection<MediaItemVm> _items = [];
    [ObservableProperty] private MediaItemVm? _selectedItem;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private double _thumbnailSize = 180;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private bool _showOnlyFavorites;
    [ObservableProperty] private bool _showHiddenContent;
    [ObservableProperty] private bool _isMultiSelectMode;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private int _filterTypeIndex;    // 0=All, 1=Photos, 2=Videos
    [ObservableProperty] private int _filterMinRating;   // 0=Any, 1-5=minimum stars

    // Unified filtering — categories shown in the Filter popup, plus the active chips.
    public ObservableCollection<FilterCategoryVm> FilterCategories { get; } = [];
    public ObservableCollection<FilterOptionVm> ActiveFilters { get; } = [];

    public bool HasActiveFilters =>
        FilterTypeIndex != 0 || FilterMinRating > 0 || ShowOnlyFavorites ||
        !string.IsNullOrWhiteSpace(SearchText) || ActiveFilters.Count > 0;

    public IReadOnlyList<long> SelectedIds =>
        Items.Where(i => i.IsSelected).Select(i => i.Id).ToList();

    private int _currentPage;
    private const int PageSize = 150;

    public LibraryViewModel(IMediaItemRepository mediaRepo, IndexOrchestrator indexer, TagService tagService,
        IPersonRepository personRepo, IPlaceRepository placeRepo, IEventRepository eventRepo,
        IAlbumRepository albumRepo, HiddenContentService? hidden = null)
    {
        _mediaRepo = mediaRepo;
        _indexer = indexer;
        _tagService = tagService;
        _personRepo = personRepo;
        _placeRepo = placeRepo;
        _eventRepo = eventRepo;
        _albumRepo = albumRepo;
        _hidden = hidden;

        if (_hidden is not null)
            _hidden.LockStateChanged += () => _ = LoadAsync();

        _ = RefreshFilterOptionsAsync();
    }

    /// <summary>Rebuilds the available filter options across every category.</summary>
    public async Task RefreshFilterOptionsAsync()
    {
        var tags    = await _tagService.GetAllTagsAsync();
        var people  = await _personRepo.GetAllAsync();
        var places  = await _placeRepo.GetAllAsync();
        var events  = await _eventRepo.GetAllAsync();
        var albums  = await _albumRepo.GetAllAsync();

        // Years are derived from the media that actually exists in the library.
        var years = (await _mediaRepo.QueryAsync(new MediaFilter(PageSize: 100_000)))
            .Where(m => m.CaptureUtc.HasValue)
            .Select(m => m.CaptureUtc!.Value.Year)
            .Distinct()
            .OrderByDescending(y => y)
            .ToList();

        FilterCategories.Clear();
        AddCategory("People", FilterKind.Person, people.Select(x => new FilterOptionVm(FilterKind.Person, x.Id, x.Name)));
        AddCategory("Places", FilterKind.Place,  places.Select(x => new FilterOptionVm(FilterKind.Place,  x.Id, x.Name)));
        AddCategory("Years",  FilterKind.Year,   years.Select(y  => new FilterOptionVm(FilterKind.Year,   y,    y.ToString())));
        AddCategory("Events", FilterKind.Event,  events.Select(x => new FilterOptionVm(FilterKind.Event,  x.Id, x.Name)));
        AddCategory("Albums", FilterKind.Album,  albums.Select(x => new FilterOptionVm(FilterKind.Album,  x.Id, x.Name)));
        AddCategory("Tags",   FilterKind.Tag,    tags.Select(x   => new FilterOptionVm(FilterKind.Tag,    x.Id, x.Name)));
    }

    private void AddCategory(string header, FilterKind kind, IEnumerable<FilterOptionVm> options)
    {
        var list = options.ToList();
        if (list.Count == 0) return; // hide empty categories from the picker
        FilterCategories.Add(new FilterCategoryVm(header, kind, list));
    }

    [RelayCommand]
    private async Task AddFilterAsync(FilterOptionVm option)
    {
        if (option is null) return;
        if (ActiveFilters.Any(f => f.Kind == option.Kind && f.Id == option.Id)) return;
        ActiveFilters.Add(option);
        OnPropertyChanged(nameof(HasActiveFilters));
        await LoadAsync();
    }

    [RelayCommand]
    private async Task RemoveFilterAsync(FilterOptionVm option)
    {
        ActiveFilters.Remove(option);
        OnPropertyChanged(nameof(HasActiveFilters));
        await LoadAsync();
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsLoading = true;
        _currentPage = 0;
        Items.Clear();
        try
        {
            var filter = BuildFilter();
            TotalCount = (int)await _mediaRepo.CountAsync(filter);
            var page = await _mediaRepo.QueryAsync(filter);
            var excluded = HiddenExcludedIds();
            bool showHidden = ShowHiddenContent && _hidden?.IsUnlocked == true;
            foreach (var m in page)
            {
                bool isHidden = excluded.Contains(m.Id);
                if (isHidden && !showHidden) continue;
                var vm = new MediaItemVm(m) { IsHiddenContent = isHidden };
                Items.Add(vm);
                vm.LoadThumbnail();
            }
            StatusText = $"{TotalCount:N0} items";
        }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (IsLoading || Items.Count >= TotalCount) return;
        IsLoading = true;
        _currentPage++;
        try
        {
            var filter = BuildFilter() with { Page = _currentPage };
            var page = await _mediaRepo.QueryAsync(filter);
            var excluded = HiddenExcludedIds();
            bool showHiddenMore = ShowHiddenContent && _hidden?.IsUnlocked == true;
            foreach (var m in page)
            {
                bool isHidden = excluded.Contains(m.Id);
                if (isHidden && !showHiddenMore) continue;
                var vm = new MediaItemVm(m) { IsHiddenContent = isHidden };
                Items.Add(vm);
                vm.LoadThumbnail();
            }
        }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task ImportFolderAsync()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Select folder to import" };
        if (dlg.ShowDialog() != true) return;

        // Show batch-tag picker — user can skip with Cancel (imports with no tags)
        var existingTags = await _tagService.GetAllTagsAsync();
        var tagDialog = new ImportTagsDialog(existingTags, _tagService);
        var batchTagIds = tagDialog.ShowDialog() == true ? tagDialog.SelectedTagIds : null;

        IsLoading = true;
        StatusText = "Importing…";
        var progress = new Progress<IndexProgress>(p =>
        {
            StatusText = $"Importing {p.Processed}/{p.Total} — {Path.GetFileName(p.CurrentFile ?? "")}";
        });
        try
        {
            await _indexer.IndexFolderAsync(dlg.FolderName, batchTagIds, progress);
            await LoadAsync();
        }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task SearchAsync() => await LoadAsync();

    [RelayCommand]
    private void ToggleMultiSelect()
    {
        IsMultiSelectMode = !IsMultiSelectMode;
        if (!IsMultiSelectMode)
        {
            foreach (var item in Items) item.IsSelected = false;
            SelectedCount = 0;
        }
    }

    [RelayCommand]
    private void ToggleItemSelection(MediaItemVm item)
    {
        if (!IsMultiSelectMode) return;
        item.IsSelected = !item.IsSelected;
        SelectedCount = Items.Count(i => i.IsSelected);
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var item in Items) item.IsSelected = true;
        SelectedCount = Items.Count;
    }

    [RelayCommand]
    private async Task ClearFiltersAsync()
    {
        SearchText = "";
        FilterTypeIndex = 0;
        FilterMinRating = 0;
        ShowOnlyFavorites = false;
        ActiveFilters.Clear();
        OnPropertyChanged(nameof(HasActiveFilters));
        await LoadAsync();
    }

    private HashSet<long> HiddenExcludedIds() =>
        _hidden is { IsUnlocked: false } ? _hidden.ExcludedMediaIds : [];

    private MediaFilter BuildFilter()
    {
        MediaType? mediaType = FilterTypeIndex switch
        {
            1 => MediaType.Photo,
            2 => MediaType.Video,
            _ => null
        };
        IReadOnlyList<long>? IdsOf(FilterKind kind)
        {
            var ids = ActiveFilters.Where(f => f.Kind == kind).Select(f => f.Id).ToList();
            return ids.Count > 0 ? ids : null;
        }
        IReadOnlyList<int>? YearsActive()
        {
            var ys = ActiveFilters.Where(f => f.Kind == FilterKind.Year)
                                  .Select(f => (int)f.Id).ToList();
            return ys.Count > 0 ? ys : null;
        }
        // When locked, exclude individually-hidden items via the DB filter
        bool? isHidden = (_hidden is { IsUnlocked: false }) ? false : null;
        return new(
            SearchText: string.IsNullOrWhiteSpace(SearchText) ? null : SearchText,
            MediaType: mediaType,
            MinRating: FilterMinRating > 0 ? FilterMinRating : null,
            IsFavorite: ShowOnlyFavorites ? true : null,
            IsHidden: isHidden,
            TagIds: IdsOf(FilterKind.Tag),
            PersonIds: IdsOf(FilterKind.Person),
            PlaceIds: IdsOf(FilterKind.Place),
            EventIds: IdsOf(FilterKind.Event),
            AlbumIds: IdsOf(FilterKind.Album),
            Years: YearsActive(),
            Page: _currentPage,
            PageSize: PageSize);
    }

    partial void OnSearchTextChanged(string value)     => _ = LoadAsync();
    partial void OnShowHiddenContentChanged(bool value) => _ = LoadAsync();
    partial void OnFilterTypeIndexChanged(int value) => _ = LoadAsync();
    partial void OnFilterMinRatingChanged(int value) => _ = LoadAsync();
    partial void OnShowOnlyFavoritesChanged(bool value) => _ = LoadAsync();
}

public enum FilterKind { Tag, Person, Place, Event, Album, Year }

/// <summary>A single selectable filter value (a tag, person, place, event, album, or year).</summary>
public sealed class FilterOptionVm(FilterKind kind, long id, string name)
{
    public FilterKind Kind { get; } = kind;
    public long   Id   { get; } = id;
    public string Name { get; } = name;

    /// <summary>Short category label shown on the active chip (e.g. "Person").</summary>
    public string KindLabel => Kind switch
    {
        FilterKind.Person => "Person",
        FilterKind.Place  => "Place",
        FilterKind.Event  => "Event",
        FilterKind.Album  => "Album",
        FilterKind.Year   => "Year",
        _                 => "Tag",
    };
}

/// <summary>A named group of filter options for the Filter popup (e.g. "People").</summary>
public sealed class FilterCategoryVm(string header, FilterKind kind, IReadOnlyList<FilterOptionVm> options)
{
    public string Header { get; } = header;
    public FilterKind Kind { get; } = kind;
    public IReadOnlyList<FilterOptionVm> Options { get; } = options;
}
