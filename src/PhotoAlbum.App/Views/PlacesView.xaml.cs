using Microsoft.Extensions.DependencyInjection;
using Microsoft.Web.WebView2.Core;
using PhotoAlbum.App.Services;
using PhotoAlbum.Core.Domain;
using PhotoAlbum.Core.Interfaces;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace PhotoAlbum.App.Views;

public partial class PlacesView : Page
{
    private IPlaceRepository? _repo;
    private Place? _selectedPlace;
    private bool _webViewReady;

    public PlacesView()
    {
        InitializeComponent();
        if (Application.Current is App app && app.Services is { } sp)
        {
            _repo = sp.GetRequiredService<IPlaceRepository>();
            _ = LoadAsync();
        }
        _ = InitWebViewAsync();
    }

    private async Task LoadAsync()
    {
        if (_repo is null) return;
        var places = await _repo.GetAllAsync();
        PlacesList.ItemsSource = places;

        // Re-select if still present
        if (_selectedPlace is not null)
        {
            var refreshed = places.FirstOrDefault(p => p.Id == _selectedPlace.Id);
            if (refreshed is not null)
            {
                _selectedPlace = refreshed;
                PlacesList.SelectedItem = refreshed;
                PopulateEditForm(refreshed);
            }
        }
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            await MapWebView.EnsureCoreWebView2Async();
            _webViewReady = true;
            MapWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            MapWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
        }
        catch
        {
            // WebView2 runtime not installed — map stays hidden gracefully
        }
    }

    // ── Place list ────────────────────────────────────────────────────────────

    private async void NewPlaceBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_repo is null) return;
        var dlg = new InputDialog("Place name:", "Add Place") { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Result)) return;
        var id = await _repo.InsertAsync(new Place { Name = dlg.Result.Trim() });
        await LoadAsync();

        // Auto-select the new place
        var newPlace = ((IEnumerable<Place>)PlacesList.ItemsSource)
            .FirstOrDefault(p => p.Id == id);
        if (newPlace is not null)
            PlacesList.SelectedItem = newPlace;
    }

    private void PlacesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlacesList.SelectedItem is not Place place) return;
        _selectedPlace = place;
        PopulateEditForm(place);
        ShowMap(place);
    }

    private async void DeletePlace_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Place place } || _repo is null) return;
        var result = MessageBox.Show(
            $"Delete place \"{place.Name}\"?\n\nPhotos assigned to this place will be unassigned but remain in your library.",
            "Delete Place", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK) return;
        await _repo.DeleteAsync(place.Id);
        if (_selectedPlace?.Id == place.Id)
        {
            _selectedPlace = null;
            ShowNoSelection();
        }
        await LoadAsync();
    }

    // ── Right panel — edit form ───────────────────────────────────────────────

    private void PopulateEditForm(Place place)
    {
        PlaceNameBox.Text = place.Name;
        LatBox.Text  = place.Latitude?.ToString("F6", CultureInfo.InvariantCulture) ?? "";
        LonBox.Text  = place.Longitude?.ToString("F6", CultureInfo.InvariantCulture) ?? "";
        CoordErrorText.Visibility = Visibility.Collapsed;

        MapPlaceholder.Visibility    = Visibility.Collapsed;
        PlaceDetailPanel.Visibility  = Visibility.Visible;

        // Show external link buttons only when coords are known
        bool hasCoords = place.Latitude.HasValue && place.Longitude.HasValue;
        OpenInBrowserBtn.Visibility   = hasCoords ? Visibility.Visible : Visibility.Collapsed;
        OpenGoogleEarthBtn.Visibility = hasCoords ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void SavePlaceBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_repo is null || _selectedPlace is null) return;

        var name = PlaceNameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            CoordErrorText.Text = "Name cannot be empty.";
            CoordErrorText.Visibility = Visibility.Visible;
            return;
        }

        // Parse lat/lon — both must be present or both absent
        double? lat = null, lon = null;
        var latText = LatBox.Text.Trim();
        var lonText = LonBox.Text.Trim();

        if (!string.IsNullOrEmpty(latText) || !string.IsNullOrEmpty(lonText))
        {
            if (!double.TryParse(latText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedLat)
                || parsedLat < -90 || parsedLat > 90)
            {
                CoordErrorText.Text = "Latitude must be a number between −90 and 90.";
                CoordErrorText.Visibility = Visibility.Visible;
                return;
            }
            if (!double.TryParse(lonText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedLon)
                || parsedLon < -180 || parsedLon > 180)
            {
                CoordErrorText.Text = "Longitude must be a number between −180 and 180.";
                CoordErrorText.Visibility = Visibility.Visible;
                return;
            }
            lat = parsedLat;
            lon = parsedLon;
        }

        CoordErrorText.Visibility = Visibility.Collapsed;

        _selectedPlace.Name      = name;
        _selectedPlace.Latitude  = lat;
        _selectedPlace.Longitude = lon;
        await _repo.UpdateAsync(_selectedPlace);

        RunLogger.Action("PlacesView", "Place saved", $"Id={_selectedPlace.Id} Name={name} Lat={lat} Lon={lon}");

        await LoadAsync();
        ShowMap(_selectedPlace);
    }

    // ── Map ───────────────────────────────────────────────────────────────────

    private void ShowMap(Place place)
    {
        if (place.Latitude is null || place.Longitude is null)
        {
            MapWebView.Visibility       = Visibility.Collapsed;
            NoCoordPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        NoCoordPlaceholder.Visibility = Visibility.Collapsed;
        MapWebView.Visibility         = Visibility.Visible;

        if (_webViewReady)
            NavigateMap(place.Latitude.Value, place.Longitude.Value);
    }

    private void ShowNoSelection()
    {
        PlaceDetailPanel.Visibility = Visibility.Collapsed;
        MapPlaceholder.Visibility   = Visibility.Visible;
        MapWebView.Visibility       = Visibility.Collapsed;
        NoCoordPlaceholder.Visibility = Visibility.Collapsed;
    }

    private void NavigateMap(double lat, double lon)
    {
        var html = $$"""
            <!DOCTYPE html>
            <html><head>
            <meta charset="utf-8"/>
            <meta name="viewport" content="width=device-width, initial-scale=1"/>
            <link rel="stylesheet" href="https://unpkg.com/leaflet@1.9.4/dist/leaflet.css"/>
            <script src="https://unpkg.com/leaflet@1.9.4/dist/leaflet.js"></script>
            <style>html,body,#map{margin:0;padding:0;height:100%;width:100%;}</style>
            </head><body>
            <div id="map"></div>
            <script>
              var map = L.map('map').setView([{{lat}}, {{lon}}], 14);
              L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
                maxZoom: 19,
                attribution: '© <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors'
              }).addTo(map);
              L.marker([{{lat}}, {{lon}}]).addTo(map)
                .bindPopup('{{lat:F5}}, {{lon:F5}}').openPopup();
            </script>
            </body></html>
            """;
        MapWebView.NavigateToString(html);
    }

    // ── External links ────────────────────────────────────────────────────────

    private void OpenInBrowserBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPlace?.Latitude is null || _selectedPlace.Longitude is null) return;
        var lat = _selectedPlace.Latitude.Value;
        var lon = _selectedPlace.Longitude.Value;
        Process.Start(new ProcessStartInfo(
            $"https://www.google.com/maps/search/?api=1&query={lat:F6},{lon:F6}")
            { UseShellExecute = true });
    }

    private void OpenGoogleEarthBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPlace?.Latitude is null || _selectedPlace.Longitude is null) return;
        var lat = _selectedPlace.Latitude.Value;
        var lon = _selectedPlace.Longitude.Value;
        // Google Earth Web — no API key required
        Process.Start(new ProcessStartInfo(
            $"https://earth.google.com/web/@{lat:F6},{lon:F6},500a,1000d,35y,0h,0t,0r")
            { UseShellExecute = true });
    }

    // ── Browse photos ─────────────────────────────────────────────────────────

    private void BrowsePhotosBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPlace is null || _repo is null) return;
        var place = _selectedPlace;
        RunLogger.Action("PlacesView", "Browse photos for place", $"PlaceId={place.Id}");
        DetailView.OriginPage = this;
        NavigationService?.Navigate(new CategoryMediaView(
            place.Name,
            sp => sp.GetRequiredService<IPlaceRepository>().GetMediaIdsAsync(place.Id)));
    }
}
