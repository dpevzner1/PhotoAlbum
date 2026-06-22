using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoAlbum.App.Services;
using PhotoAlbum.Core.Domain;
using PhotoAlbum.Core.Interfaces;
using System.IO;
using System.Windows.Media.Imaging;

namespace PhotoAlbum.App.ViewModels;

public sealed partial class DetailViewModel : ObservableObject
{
    private readonly IMediaItemRepository _mediaRepo;
    private readonly ITagRepository _tagRepo;
    private readonly IPersonRepository _personRepo;
    private readonly IPlaceRepository _placeRepo;
    private readonly IAlbumRepository _albumRepo;
    private readonly IOperationLogRepository _opLog;

    [ObservableProperty] private MediaItem? _item;
    [ObservableProperty] private BitmapImage? _fullImage;
    [ObservableProperty] private IReadOnlyList<Tag> _tags = [];
    [ObservableProperty] private IReadOnlyList<Person> _people = [];
    [ObservableProperty] private IReadOnlyList<Place> _places = [];
    [ObservableProperty] private IReadOnlyList<Album> _albums = [];
    [ObservableProperty] private string? _currentFilePath;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private int _rotationDegrees;

    public DetailViewModel(
        IMediaItemRepository mediaRepo,
        ITagRepository tagRepo,
        IPersonRepository personRepo,
        IPlaceRepository placeRepo,
        IAlbumRepository albumRepo,
        IOperationLogRepository opLog)
    {
        _mediaRepo = mediaRepo;
        _tagRepo = tagRepo;
        _personRepo = personRepo;
        _placeRepo = placeRepo;
        _albumRepo = albumRepo;
        _opLog = opLog;
    }

    public async Task LoadAsync(long mediaItemId, string filePath, CancellationToken ct = default)
    {
        IsLoading = true;
        try
        {
            Item = await _mediaRepo.GetByIdAsync(mediaItemId, ct);
            RotationDegrees = Item?.RotationDegrees ?? 0;

            // Always resolve the canonical file path from the DB; the caller may pass a thumbnail path
            var dbPath = await _mediaRepo.GetPrimaryFilePathAsync(mediaItemId, ct);
            filePath = dbPath ?? filePath;
            CurrentFilePath = filePath;
            Tags   = await _tagRepo.GetTagsForMediaAsync(mediaItemId, ct);
            People = await _personRepo.GetPeopleForMediaAsync(mediaItemId, ct);
            Places = await _placeRepo.GetPlacesForMediaAsync(mediaItemId, ct);
            Albums = await _albumRepo.GetAlbumsForMediaAsync(mediaItemId, ct);

            if (File.Exists(filePath))
            {
                await Task.Run(() =>
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(filePath);
                    bmp.EndInit();
                    bmp.Freeze();
                    FullImage = bmp;
                }, ct);
            }
        }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync()
    {
        if (Item is null) return;
        Item.IsFavorite = !Item.IsFavorite;
        await _mediaRepo.UpdateAsync(Item);
        await _opLog.LogAsync("ToggleFavorite", "MediaItem", Item.Id, Item.IsFavorite.ToString());
        OnPropertyChanged(nameof(Item));
    }

    [RelayCommand]
    private async Task SetRatingAsync(int rating)
    {
        if (Item is null) return;
        Item.Rating = rating;
        await _mediaRepo.UpdateAsync(Item);
        await _opLog.LogAsync("SetRating", "MediaItem", Item.Id, rating.ToString());
        OnPropertyChanged(nameof(Item));
    }

    [RelayCommand]
    private async Task SoftDeleteAsync()
    {
        if (Item is null) return;
        await _mediaRepo.SoftDeleteAsync(Item.Id, DeleteMode.Trash);
        await _opLog.LogAsync("Delete", "MediaItem", Item.Id, "Trash");
    }

    /// <summary>Rotate the displayed image 90° clockwise and persist to DB.</summary>
    [RelayCommand]
    private async Task RotateClockwiseAsync()
    {
        if (Item is null) return;
        RotationDegrees = (RotationDegrees + 90) % 360;
        Item.RotationDegrees = RotationDegrees;
        await _mediaRepo.SetRotationAsync(Item.Id, RotationDegrees);
        await _opLog.LogAsync("Rotate", "MediaItem", Item.Id, RotationDegrees.ToString());
    }
}
