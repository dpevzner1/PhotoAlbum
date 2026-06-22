using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoAlbum.Core.Domain;
using PhotoAlbum.Core.Interfaces;

namespace PhotoAlbum.App.ViewModels;

public sealed class CheckableItem(long id, string name)
{
    public long Id { get; } = id;
    public string Name { get; } = name;
    public bool IsChecked { get; set; }
}

public sealed partial class BulkEditViewModel : ObservableObject
{
    private readonly IMediaItemRepository _mediaRepo;
    private readonly ITagRepository _tagRepo;
    private readonly IPersonRepository _personRepo;
    private readonly IPlaceRepository _placeRepo;
    private readonly IOperationLogRepository _opLog;
    private readonly IReadOnlyList<long> _selectedIds;

    [ObservableProperty] private IReadOnlyList<CheckableItem> _tagChoices = [];
    [ObservableProperty] private IReadOnlyList<CheckableItem> _personChoices = [];
    [ObservableProperty] private IReadOnlyList<CheckableItem> _placeChoices = [];
    [ObservableProperty] private int? _newRating;
    [ObservableProperty] private bool _setFavorite;
    [ObservableProperty] private bool _setHidden;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "";

    public int SelectionCount => _selectedIds.Count;

    public BulkEditViewModel(
        IReadOnlyList<long> selectedIds,
        IMediaItemRepository mediaRepo,
        ITagRepository tagRepo,
        IPersonRepository personRepo,
        IPlaceRepository placeRepo,
        IOperationLogRepository opLog)
    {
        _selectedIds = selectedIds;
        _mediaRepo = mediaRepo;
        _tagRepo = tagRepo;
        _personRepo = personRepo;
        _placeRepo = placeRepo;
        _opLog = opLog;
    }

    public async Task LoadAsync()
    {
        var tags = await _tagRepo.GetAllAsync();
        TagChoices = tags.Select(t => new CheckableItem(t.Id, t.Name)).ToList();

        var people = await _personRepo.GetAllAsync();
        PersonChoices = people.Select(p => new CheckableItem(p.Id, p.Name)).ToList();

        var places = await _placeRepo.GetAllAsync();
        PlaceChoices = places.Select(p => new CheckableItem(p.Id, p.Name)).ToList();
    }

    public async Task<CheckableItem> CreateTagAsync(string name)
    {
        var id = await _tagRepo.InsertAsync(new Tag { Name = name.Trim() });
        var item = new CheckableItem(id, name.Trim()) { IsChecked = true };
        TagChoices = [.. TagChoices, item];
        return item;
    }

    public async Task<CheckableItem> CreatePersonAsync(string name)
    {
        var id = await _personRepo.InsertAsync(new Person { Name = name.Trim() });
        var item = new CheckableItem(id, name.Trim()) { IsChecked = true };
        PersonChoices = [.. PersonChoices, item];
        return item;
    }

    public async Task<CheckableItem> CreatePlaceAsync(string name)
    {
        var id = await _placeRepo.InsertAsync(new Place { Name = name.Trim() });
        var item = new CheckableItem(id, name.Trim()) { IsChecked = true };
        PlaceChoices = [.. PlaceChoices, item];
        return item;
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        IsBusy = true;
        int done = 0;
        var checkedTags    = TagChoices.Where(t => t.IsChecked).Select(t => t.Id).ToList();
        var checkedPeople  = PersonChoices.Where(p => p.IsChecked).Select(p => p.Id).ToList();
        var checkedPlaces  = PlaceChoices.Where(p => p.IsChecked).Select(p => p.Id).ToList();

        foreach (var id in _selectedIds)
        {
            var item = await _mediaRepo.GetByIdAsync(id);
            if (item is null) continue;

            if (NewRating.HasValue && NewRating.Value >= 0) item.Rating = NewRating.Value;
            if (SetFavorite) item.IsFavorite = true;
            if (SetHidden)   item.IsHidden   = true;
            await _mediaRepo.UpdateAsync(item);

            foreach (var tagId in checkedTags)
                await _tagRepo.TagMediaAsync(id, tagId);

            foreach (var personId in checkedPeople)
                await _personRepo.TagMediaAsync(id, personId);

            foreach (var placeId in checkedPlaces)
                await _placeRepo.AssignMediaAsync(id, placeId);

            await _opLog.LogAsync("BulkEdit", "MediaItem", id, null);
            done++;
        }
        StatusText = $"Updated {done} items.";
        IsBusy = false;
    }

    [RelayCommand]
    private async Task BulkDeleteAsync()
    {
        IsBusy = true;
        foreach (var id in _selectedIds)
        {
            await _mediaRepo.SoftDeleteAsync(id, DeleteMode.Trash);
            await _opLog.LogAsync("BulkDelete", "MediaItem", id, null);
        }
        StatusText = $"Moved {_selectedIds.Count} items to trash.";
        IsBusy = false;
    }
}
