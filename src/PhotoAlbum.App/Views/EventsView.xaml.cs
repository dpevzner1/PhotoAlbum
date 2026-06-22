using Microsoft.Extensions.DependencyInjection;
using PhotoAlbum.App.Services;
using PhotoAlbum.Core.Domain;
using PhotoAlbum.Core.Interfaces;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PhotoAlbum.App.Views;

public partial class EventsView : Page
{
    private IEventRepository? _repo;

    public EventsView()
    {
        InitializeComponent();
        if (System.Windows.Application.Current is App app && app.Services is { } sp)
        {
            _repo = sp.GetRequiredService<IEventRepository>();
            _ = LoadAsync();
        }
    }

    private async Task LoadAsync()
    {
        if (_repo is null) return;
        var events = await _repo.GetAllAsync();
        EventsList.ItemsSource = events;
        RunLogger.Info("EventsView", "Events loaded", $"Count={events.Count}");
    }

    private async void NewEventBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_repo is null) return;
        RunLogger.Action("EventsView", "New Event button clicked");
        var dlg = new InputDialog("Event name:", "New Event")
            { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Result)) return;
        var name = dlg.Result.Trim();
        RunLogger.Action("EventsView", "Creating event", $"Name=\"{name}\"");
        await _repo.InsertAsync(new Event { Name = name });
        await LoadAsync();
    }

    private async void RenameEvent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Event ev } || _repo is null) return;
        RunLogger.Action("EventsView", "Rename event clicked", $"EventId={ev.Id}");
        var dlg = new InputDialog("New event name:", "Rename Event", ev.Name)
            { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Result)) return;
        ev.Name = dlg.Result.Trim();
        await _repo.UpdateAsync(ev);
        RunLogger.Info("EventsView", "Event renamed", $"EventId={ev.Id}  Name=\"{ev.Name}\"");
        await LoadAsync();
    }

    private async void DeleteEvent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Event ev } || _repo is null) return;
        RunLogger.Action("EventsView", "Delete event clicked", $"EventId={ev.Id}");
        var result = MessageBox.Show(
            $"Delete event \"{ev.Name}\"?\n\nPhotos assigned to this event will be unassigned but remain in your library.",
            "Delete Event", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK) return;
        await _repo.DeleteAsync(ev.Id);
        RunLogger.Info("EventsView", "Event deleted", $"EventId={ev.Id}");
        await LoadAsync();
    }

    private void EventRow_Click(object sender, MouseButtonEventArgs e)
    {
        var src = e.OriginalSource as DependencyObject;
        while (src is not null) { if (src is Button) return; src = VisualTreeHelper.GetParent(src); }
        if (sender is not FrameworkElement { DataContext: Event ev } || _repo is null) return;
        RunLogger.Action("EventsView", "Event row clicked — browse photos", $"EventId={ev.Id}");
        DetailView.OriginPage = this;
        NavigationService?.Navigate(new CategoryMediaView(
            ev.Name,
            sp => sp.GetRequiredService<IEventRepository>().GetMediaIdsAsync(ev.Id)));
    }
}
