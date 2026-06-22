using Microsoft.Extensions.DependencyInjection;
using PhotoAlbum.App.Services;
using PhotoAlbum.Core.Domain;
using PhotoAlbum.Core.Interfaces;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace PhotoAlbum.App.Views;

public partial class CloneView : Page
{
    private ExportService? _exporter;
    private CancellationTokenSource? _cts;

    private IReadOnlyList<Album>  _albums = [];
    private IReadOnlyList<Person> _people = [];
    private IReadOnlyList<Tag>    _tags   = [];
    private IReadOnlyList<Event>  _events = [];

    // Tracks checked IDs for the custom filter
    private readonly HashSet<long> _selAlbums  = [];
    private readonly HashSet<long> _selPeople  = [];
    private readonly HashSet<long> _selTags    = [];
    private readonly HashSet<long> _selEvents  = [];

    public CloneView()
    {
        InitializeComponent();
        if (Application.Current is App app && app.Services is { } sp)
        {
            _exporter = sp.GetRequiredService<ExportService>();
            _ = PreloadScopeDataAsync(sp);
        }
    }

    private async Task PreloadScopeDataAsync(IServiceProvider sp)
    {
        _albums = await sp.GetRequiredService<IAlbumRepository>().GetAllAsync();
        _people = await sp.GetRequiredService<IPersonRepository>().GetAllAsync();
        _tags   = await sp.GetRequiredService<ITagRepository>().GetAllAsync();
        _events = await sp.GetRequiredService<IEventRepository>().GetAllAsync();

        // Pre-populate the custom filter lists so checkboxes are ready
        FilterAlbumsList.ItemsSource  = _albums;
        FilterPeopleList.ItemsSource  = _people;
        FilterTagsList.ItemsSource    = _tags;
        FilterEventsList.ItemsSource  = _events;
    }

    // ── scope radio selection ─────────────────────────────────────────────────

    private void Scope_Checked(object sender, RoutedEventArgs e)
    {
        if (FolderPickerRow is null) return;

        FolderPickerRow.Visibility = ScopeFolder.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;

        ScopeCombo.Visibility = (ScopeAlbum.IsChecked == true
                              || ScopePerson.IsChecked == true
                              || ScopeTag.IsChecked == true)
            ? Visibility.Visible : Visibility.Collapsed;

        CustomFilterPanel.Visibility = ScopeCustom.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;

        if (ScopeAlbum.IsChecked == true)
        {
            ScopeCombo.ItemsSource   = _albums;
            ScopeCombo.SelectedIndex = _albums.Count > 0 ? 0 : -1;
        }
        else if (ScopePerson.IsChecked == true)
        {
            ScopeCombo.ItemsSource   = _people;
            ScopeCombo.SelectedIndex = _people.Count > 0 ? 0 : -1;
        }
        else if (ScopeTag.IsChecked == true)
        {
            ScopeCombo.ItemsSource   = _tags;
            ScopeCombo.SelectedIndex = _tags.Count > 0 ? 0 : -1;
        }

        UpdateStartEnabled();
    }

    private void ScopeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateStartEnabled();

    // ── custom filter checkboxes ──────────────────────────────────────────────

    private void FilterAlbum_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: long id }) _selAlbums.Add(id);
        UpdateFilterSummary(); UpdateStartEnabled();
    }
    private void FilterAlbum_Unchecked(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: long id }) _selAlbums.Remove(id);
        UpdateFilterSummary(); UpdateStartEnabled();
    }

    private void FilterPerson_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: long id }) _selPeople.Add(id);
        UpdateFilterSummary(); UpdateStartEnabled();
    }
    private void FilterPerson_Unchecked(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: long id }) _selPeople.Remove(id);
        UpdateFilterSummary(); UpdateStartEnabled();
    }

    private void FilterTag_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: long id }) _selTags.Add(id);
        UpdateFilterSummary(); UpdateStartEnabled();
    }
    private void FilterTag_Unchecked(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: long id }) _selTags.Remove(id);
        UpdateFilterSummary(); UpdateStartEnabled();
    }

    private void FilterEvent_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: long id }) _selEvents.Add(id);
        UpdateFilterSummary(); UpdateStartEnabled();
    }
    private void FilterEvent_Unchecked(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: long id }) _selEvents.Remove(id);
        UpdateFilterSummary(); UpdateStartEnabled();
    }

    private void UpdateFilterSummary()
    {
        if (FilterSelectionSummary is null) return;
        int total = _selAlbums.Count + _selPeople.Count + _selTags.Count + _selEvents.Count;
        if (total == 0)
        {
            FilterSelectionSummary.Visibility = Visibility.Collapsed;
            return;
        }
        var parts = new List<string>();
        if (_selAlbums.Count  > 0) parts.Add($"{_selAlbums.Count} album{(_selAlbums.Count  > 1 ? "s" : "")}");
        if (_selPeople.Count  > 0) parts.Add($"{_selPeople.Count} person{(_selPeople.Count  > 1 ? "s" : "")}");
        if (_selTags.Count    > 0) parts.Add($"{_selTags.Count} tag{(_selTags.Count    > 1 ? "s" : "")}");
        if (_selEvents.Count  > 0) parts.Add($"{_selEvents.Count} event{(_selEvents.Count  > 1 ? "s" : "")}");
        FilterSelectionSummary.Text = $"Selected: {string.Join(", ", parts)}";
        FilterSelectionSummary.Visibility = Visibility.Visible;
    }

    private void BrowseScopeFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Select folder to export" };
        if (dlg.ShowDialog() == true)
        {
            ScopeFolderBox.Text = dlg.FolderName;
            UpdateStartEnabled();
        }
    }

    private void BrowseDestBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Select export destination" };
        if (dlg.ShowDialog() == true)
        {
            DestBox.Text = dlg.FolderName;
            UpdateStartEnabled();
        }
    }

    private void UpdateStartEnabled()
    {
        if (StartBtn is null) return;
        int customSelCount = _selAlbums.Count + _selPeople.Count + _selTags.Count + _selEvents.Count;
        bool scopeOk = ScopeAll?.IsChecked == true
            || (ScopeFolder?.IsChecked == true && !string.IsNullOrEmpty(ScopeFolderBox?.Text))
            || ((ScopeAlbum?.IsChecked == true || ScopePerson?.IsChecked == true || ScopeTag?.IsChecked == true)
                && ScopeCombo?.SelectedItem is not null)
            || (ScopeCustom?.IsChecked == true && customSelCount > 0);
        StartBtn.IsEnabled = scopeOk && !string.IsNullOrEmpty(DestBox?.Text) && _cts is null;
    }

    // ── export execution ──────────────────────────────────────────────────────

    private async void StartBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_exporter is null) return;
        var spec = BuildSpec();
        if (spec is null) return;

        ProgressBar.Value        = 0;
        ProgressBar.Maximum      = 100;
        ProgressPanel.Visibility = Visibility.Visible;
        DoneBanner.Visibility    = Visibility.Collapsed;
        FailedText.Visibility    = Visibility.Collapsed;
        ProgressHeaderText.Text  = "Exporting…";
        PhaseText.Text           = "";
        StartBtn.IsEnabled       = false;
        CancelBtn.IsEnabled      = true;
        BrowseDestBtn.IsEnabled  = false;

        _cts = new CancellationTokenSource();

        var progress = new Progress<ExportProgress>(p =>
        {
            PhaseText.Text         = p.Phase;
            CurrentFileText.Text   = p.CurrentFile is not null ? Path.GetFileName(p.CurrentFile) : "";
            ProgressCountText.Text = $"{p.Completed + p.Failed}/{p.Total}";
            if (p.Total > 0)
                ProgressBar.Value = (double)(p.Completed + p.Failed) / p.Total * 100;
            if (p.Failed > 0)
            {
                FailedText.Visibility = Visibility.Visible;
                FailedText.Text       = $"{p.Failed} file(s) could not be copied.";
            }
        });

        RunLogger.Action("CloneView", "Export started",
            $"scope={spec.Scope} dest={spec.DestRoot} appBundled={spec.IncludeAppBinary}");

        try
        {
            await _exporter.ExecuteAsync(spec, progress, _cts.Token);
            ProgressHeaderText.Text = "Complete";
            DoneBanner.Visibility   = Visibility.Visible;
            DoneText.Text = $"Export complete — {spec.DestRoot}"
                + (spec.IncludeAppBinary
                    ? "\nApplication bundled. Run PhotoAlbum\\PhotoAlbum.App.exe on the destination machine."
                    : "");
            RunLogger.Action("CloneView", "Export complete", $"dest={spec.DestRoot}");
        }
        catch (OperationCanceledException)
        {
            ProgressHeaderText.Text = "Cancelled";
            RunLogger.Info("CloneView", "Export cancelled by user");
        }
        catch (Exception ex)
        {
            ProgressHeaderText.Text = "Error";
            RunLogger.Error("CloneView", "Export failed", ex);
            MessageBox.Show($"Export failed: {ex.Message}", "Export Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            CancelBtn.IsEnabled     = false;
            BrowseDestBtn.IsEnabled = true;
            UpdateStartEnabled();
        }
    }

    private ExportSpec? BuildSpec()
    {
        var dest = DestBox.Text;
        if (string.IsNullOrEmpty(dest)) return null;

        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhotoAlbum", "album.db");

        ExportScope scope;
        long scopeId = 0;
        string? folderFilter = null;

        if (ScopeFolder.IsChecked == true)
        {
            scope        = ExportScope.ByFolder;
            folderFilter = ScopeFolderBox.Text;
        }
        else if (ScopeAlbum.IsChecked == true && ScopeCombo.SelectedItem is Album album)
        {
            scope   = ExportScope.ByAlbum;
            scopeId = album.Id;
        }
        else if (ScopePerson.IsChecked == true && ScopeCombo.SelectedItem is Person person)
        {
            scope   = ExportScope.ByPerson;
            scopeId = person.Id;
        }
        else if (ScopeTag.IsChecked == true && ScopeCombo.SelectedItem is Tag tag)
        {
            scope   = ExportScope.ByTag;
            scopeId = tag.Id;
        }
        else if (ScopeCustom.IsChecked == true)
        {
            scope = ExportScope.ByFilter;
        }
        else
        {
            scope = ExportScope.All;
        }

        return new ExportSpec(
            DestRoot:         dest,
            Scope:            scope,
            ScopeId:          scopeId,
            FolderFilter:     folderFilter,
            IncludeAppBinary: IncludeAppChk.IsChecked == true,
            DatabasePath:     dbPath,
            FilterAlbumIds:   _selAlbums.Count  > 0 ? [.. _selAlbums]  : null,
            FilterPersonIds:  _selPeople.Count  > 0 ? [.. _selPeople]  : null,
            FilterTagIds:     _selTags.Count    > 0 ? [.. _selTags]    : null,
            FilterEventIds:   _selEvents.Count  > 0 ? [.. _selEvents]  : null);
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        CancelBtn.IsEnabled = false;
    }
}
