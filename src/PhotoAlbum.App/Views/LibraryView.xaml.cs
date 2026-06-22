using Microsoft.Extensions.DependencyInjection;
using PhotoAlbum.App.Services;
using PhotoAlbum.App.ViewModels;
using PhotoAlbum.Core.Domain;
using PhotoAlbum.Core.Interfaces;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;

namespace PhotoAlbum.App.Views;

public partial class LibraryView : Page
{
    private LibraryViewModel? _vm;
    private HiddenContentService? _hidden;
    private bool _firstNavigationDone;

    public LibraryView()
    {
        InitializeComponent();
        if (System.Windows.Application.Current is App app && app.Services is { } sp)
        {
            _vm     = sp.GetRequiredService<LibraryViewModel>();
            _hidden = sp.GetService<HiddenContentService>();
            DataContext = _vm;
            _ = _vm.LoadCommand.ExecuteAsync(null);

            // Show the "Show Hidden" toggle when there is hidden content
            if (_hidden is not null)
            {
                _hidden.HiddenSetChanged += () => Dispatcher.Invoke(UpdateShowHiddenVisibility);
                _hidden.LockStateChanged += () => Dispatcher.Invoke(UpdateShowHiddenVisibility);
                UpdateShowHiddenVisibility();
            }
        }

        // Reload whenever navigation returns to this page (e.g., back from DetailView after delete).
        // Skip the very first navigation — the constructor already kicked off the initial load.
        Loaded   += (_, _) => { if (NavigationService != null) NavigationService.Navigated += OnNavigated; };
        Unloaded += (_, _) => { if (NavigationService != null) NavigationService.Navigated -= OnNavigated; };
    }

    private async void OnNavigated(object sender, NavigationEventArgs e)
    {
        if (e.Content != this || _vm is null) return;
        if (!_firstNavigationDone) { _firstNavigationDone = true; return; }
        RunLogger.Info("LibraryView", "Navigated back to LibraryView — reloading items");
        await _vm.LoadCommand.ExecuteAsync(null);
    }

    // Drag-select: if left button is held while dragging over thumbnails, select each one
    private void Thumb_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_vm is null || !_vm.IsMultiSelectMode) return;
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (sender is FrameworkElement fe && fe.DataContext is MediaItemVm item && !item.IsSelected)
            _vm.ToggleItemSelectionCommand.Execute(item);
    }

    private void UpdateShowHiddenVisibility()
    {
        if (ShowHiddenBtn is null || _hidden is null) return;
        ShowHiddenBtn.Visibility = _hidden.ExcludedMediaIds.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ShowHiddenBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null || _hidden is null) return;

        bool turningOn = !_vm.ShowHiddenContent;

        if (turningOn && !_hidden.IsUnlocked)
        {
            if (_hidden.HasPin)
            {
                var dlg = new InputDialog("Enter PIN to reveal hidden content:", "Hidden Content")
                    { Owner = Window.GetWindow(this) };
                if (dlg.ShowDialog() != true || string.IsNullOrEmpty(dlg.Result))
                {
                    ShowHiddenBtn.IsChecked = false;
                    return;
                }
                if (!await _hidden.TryUnlockWithPinAsync(dlg.Result))
                {
                    MessageBox.Show("Incorrect PIN.", "Access Denied",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    ShowHiddenBtn.IsChecked = false;
                    return;
                }
            }
            else
            {
                _hidden.TryUnlockNoPin();
            }
        }

        _vm.ShowHiddenContent = turningOn;
        ShowHiddenBtn.IsChecked = turningOn;
    }

    private void Thumb_Click(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null || sender is not FrameworkElement fe || fe.DataContext is not MediaItemVm item)
            return;

        if (_vm.IsMultiSelectMode)
        {
            RunLogger.Action("LibraryView", "Thumbnail clicked — toggle select",
                $"MediaItemId={item.Id}  IsSelected={!item.IsSelected}");
            _vm.ToggleItemSelectionCommand.Execute(item);
            return;
        }

        RunLogger.Action("LibraryView", "Thumbnail clicked — open detail",
            $"MediaItemId={item.Id}");
        OpenDetail(item);
    }

    private void OpenDetail(MediaItemVm item)
    {
        if (_vm is null) return;
        DetailView.NavigationContext = _vm.Items
            .Select(i => (i.Id, i.ThumbnailPath ?? ""))
            .ToList();
        DetailView.NavigationIndex = _vm.Items.IndexOf(item);
        RunLogger.Info("LibraryView", "Navigating to DetailView",
            $"MediaItemId={item.Id}  Index={DetailView.NavigationIndex}/{DetailView.NavigationContext?.Count}");
        NavigationService?.Navigate(new DetailView(item.Id, item.ThumbnailPath ?? ""));
    }

    // ── Bulk action handlers ─────────────────────────────────────────────────

    private void BulkTagBtn_Click(object sender, RoutedEventArgs e)
    {
        RunLogger.Action("LibraryView", "Bulk Tag/Label button clicked",
            $"SelectedCount={_vm?.SelectedCount}");
        OpenBulkEditDialog();
    }

    private async void BulkAlbumBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null || _vm.SelectedCount == 0) return;
        RunLogger.Action("LibraryView", "Bulk Add to Album clicked",
            $"SelectedCount={_vm.SelectedCount}");
        if (System.Windows.Application.Current is not App app || app.Services is not { } sp) return;

        var albumRepo = sp.GetRequiredService<IAlbumRepository>();
        var albums = await albumRepo.GetAllAsync();

        if (albums.Count == 0)
        {
            MessageBox.Show("No albums found. Create an album first in the Albums view.",
                "No Albums", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Simple album picker dialog
        var dlg = new AlbumPickerDialog(albums) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || dlg.SelectedAlbumId is not { } albumId)
        {
            RunLogger.Info("LibraryView", "Add to Album cancelled or no selection");
            return;
        }

        RunLogger.Action("LibraryView", "Adding selected items to album",
            $"AlbumId={albumId}  Count={_vm.SelectedCount}");
        var opLog = sp.GetRequiredService<IOperationLogRepository>();
        foreach (var id in _vm.SelectedIds)
        {
            await albumRepo.AddMediaAsync(albumId, id);
            await opLog.LogAsync("AddToAlbum", "MediaItem", id, albumId.ToString());
        }
        RunLogger.Info("LibraryView", "Bulk add to album complete",
            $"AlbumId={albumId}  Count={_vm.SelectedCount}");
        MessageBox.Show($"Added {_vm.SelectedCount} photo(s) to album.", "Done",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BulkPeopleBtn_Click(object sender, RoutedEventArgs e)
    {
        RunLogger.Action("LibraryView", "Bulk People button clicked",
            $"SelectedCount={_vm?.SelectedCount}");
        OpenBulkEditDialog();
    }

    private void BulkPlaceEventBtn_Click(object sender, RoutedEventArgs e)
    {
        RunLogger.Action("LibraryView", "Bulk Place/Event button clicked",
            $"SelectedCount={_vm?.SelectedCount}");
        OpenBulkEditDialog();
    }

    private void BulkEditBtn_Click(object sender, RoutedEventArgs e) => OpenBulkEditDialog();

    private void OpenBulkEditDialog()
    {
        if (_vm is null || _vm.SelectedCount == 0) return;
        if (Application.Current is not App app || app.Services is not { } sp) return;

        RunLogger.Info("LibraryView", "Opening BulkEditDialog",
            $"Ids=[{string.Join(",", _vm.SelectedIds)}]");

        var bulkVm = new BulkEditViewModel(
            _vm.SelectedIds,
            sp.GetRequiredService<IMediaItemRepository>(),
            sp.GetRequiredService<ITagRepository>(),
            sp.GetRequiredService<IPersonRepository>(),
            sp.GetRequiredService<IPlaceRepository>(),
            sp.GetRequiredService<IOperationLogRepository>());

        var dlg = new BulkEditDialog(bulkVm) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true)
        {
            RunLogger.Info("LibraryView", "BulkEditDialog closed with Apply — reloading");
            _ = _vm.LoadCommand.ExecuteAsync(null);
        }
        else
        {
            RunLogger.Info("LibraryView", "BulkEditDialog cancelled");
        }
    }

    private async void FilterBtn_Click(object sender, RoutedEventArgs e)
    {
        // Refresh available options each time the popup opens so newly created
        // people/places/events/albums/tags/years appear without restarting.
        if (!FilterPopup.IsOpen && _vm is not null)
            await _vm.RefreshFilterOptionsAsync();
        FilterPopup.IsOpen = !FilterPopup.IsOpen;
    }

    private void FilterOptionItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is FilterOptionVm option && _vm is not null)
        {
            _vm.AddFilterCommand.Execute(option);
            FilterPopup.IsOpen = false;
        }
    }

    private async void BulkDeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null || _vm.SelectedCount == 0) return;

        RunLogger.Action("LibraryView", "Bulk Delete clicked (confirmation pending)",
            $"SelectedCount={_vm.SelectedCount}  Ids=[{string.Join(",", _vm.SelectedIds)}]");

        var confirm = MessageBox.Show(
            $"Move {_vm.SelectedCount} photo(s) to trash?",
            "Confirm Delete", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK)
        {
            RunLogger.Info("LibraryView", "Bulk delete cancelled by user");
            return;
        }
        RunLogger.Action("LibraryView", "Bulk delete confirmed — soft deleting to Trash",
            $"Count={_vm.SelectedCount}");

        if (System.Windows.Application.Current is App app && app.Services is { } sp)
        {
            var mediaRepo = sp.GetRequiredService<Core.Interfaces.IMediaItemRepository>();
            var opLog = sp.GetRequiredService<Core.Interfaces.IOperationLogRepository>();
            foreach (var id in _vm.SelectedIds)
            {
                await mediaRepo.SoftDeleteAsync(id, Core.Domain.DeleteMode.Trash);
                await opLog.LogAsync("BulkDelete", "MediaItem", id, null);
            }
            _vm.ToggleMultiSelectCommand.Execute(null);
            await _vm.LoadCommand.ExecuteAsync(null);
        }
    }
}
