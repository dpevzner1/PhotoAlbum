using Microsoft.Extensions.DependencyInjection;
using PhotoAlbum.App.Services;
using PhotoAlbum.Core.Domain;
using PhotoAlbum.Core.Interfaces;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PhotoAlbum.App.Views;

public partial class PeopleView : Page
{
    private IPersonRepository? _repo;

    public PeopleView()
    {
        InitializeComponent();
        if (System.Windows.Application.Current is App app && app.Services is { } sp)
        {
            _repo = sp.GetRequiredService<IPersonRepository>();
            _ = LoadAsync();
        }
    }

    private async Task LoadAsync()
    {
        if (_repo is null) return;
        var people = await _repo.GetAllAsync();
        PeopleList.ItemsSource = people;
        RunLogger.Info("PeopleView", "People loaded", $"Count={people.Count}");
    }

    private async void NewPersonBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_repo is null) return;
        RunLogger.Action("PeopleView", "Add Person button clicked");
        var dlg = new InputDialog("Person name:", "Add Person")
            { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Result)) return;
        var name = dlg.Result.Trim();
        RunLogger.Action("PeopleView", "Creating person", $"Name=\"{name}\"");
        await _repo.InsertAsync(new Person { Name = name });
        await LoadAsync();
    }

    private async void RenamePerson_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Person person } || _repo is null) return;
        RunLogger.Action("PeopleView", "Rename person clicked", $"PersonId={person.Id}");
        var dlg = new InputDialog("New name:", "Rename Person", person.Name)
            { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Result)) return;
        person.Name = dlg.Result.Trim();
        await _repo.UpdateAsync(person);
        RunLogger.Info("PeopleView", "Person renamed", $"PersonId={person.Id}  Name=\"{person.Name}\"");
        await LoadAsync();
    }

    private async void DeletePerson_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Person person } || _repo is null) return;
        RunLogger.Action("PeopleView", "Delete person clicked", $"PersonId={person.Id}");
        var result = MessageBox.Show(
            $"Remove \"{person.Name}\" from your People list?\n\nPhotos tagged with this person will be untagged but remain in your library.",
            "Remove Person", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK) return;
        await _repo.DeleteAsync(person.Id);
        RunLogger.Info("PeopleView", "Person deleted", $"PersonId={person.Id}");
        await LoadAsync();
    }

    private void PersonCard_Click(object sender, MouseButtonEventArgs e)
    {
        // Ignore clicks that land on action buttons inside the card
        var src = e.OriginalSource as DependencyObject;
        while (src is not null) { if (src is Button) return; src = VisualTreeHelper.GetParent(src); }

        if (sender is not FrameworkElement { DataContext: Person person } || _repo is null) return;
        RunLogger.Action("PeopleView", "Person card clicked — browse photos", $"PersonId={person.Id}");
        DetailView.OriginPage = this;
        NavigationService?.Navigate(new CategoryMediaView(
            person.Name,
            sp => sp.GetRequiredService<IPersonRepository>().GetMediaIdsAsync(person.Id)));
    }
}
