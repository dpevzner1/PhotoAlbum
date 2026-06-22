using PhotoAlbum.App.Services;
using PhotoAlbum.Core.Domain;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace PhotoAlbum.App.Views;

public partial class ImportTagsDialog : Window
{
    private readonly TagService _tagService;
    private readonly List<TagChipState> _chips = [];

    public IReadOnlyList<long> SelectedTagIds =>
        _chips.Where(c => c.IsSelected).Select(c => c.Tag.Id).ToList();

    public ImportTagsDialog(IReadOnlyList<Tag> existingTags, TagService tagService)
    {
        InitializeComponent();
        _tagService = tagService;

        if (existingTags.Count == 0)
        {
            ExistingTagsPanel.Children.Add(new TextBlock
            {
                Text = "No tags yet — create one below.",
                Foreground = (Brush)Application.Current.Resources["SecondaryTextBrush"],
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        else
        {
            foreach (var tag in existingTags)
                AddChip(tag, selected: false);
        }

        UpdateSummary();
    }

    private void AddChip(Tag tag, bool selected)
    {
        var state = new TagChipState(tag);
        _chips.Add(state);

        var btn = new ToggleButton
        {
            Content = tag.Name,
            IsChecked = selected,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 6, 6),
            Tag = state,
        };
        System.Windows.Automation.AutomationProperties.SetName(btn, $"Tag: {tag.Name}");

        btn.Checked   += (_, _) => { state.IsSelected = true;  UpdateSummary(); };
        btn.Unchecked += (_, _) => { state.IsSelected = false; UpdateSummary(); };

        if (selected) state.IsSelected = true;

        ExistingTagsPanel.Children.Add(btn);
    }

    private async void AddTagBtn_Click(object sender, RoutedEventArgs e)
    {
        var name = NewTagBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;

        AddTagBtn.IsEnabled = false;
        try
        {
            var tag = await _tagService.CreateTagAsync(name);

            // Remove placeholder text if present
            if (ExistingTagsPanel.Children.Count == 1
                && ExistingTagsPanel.Children[0] is TextBlock)
                ExistingTagsPanel.Children.Clear();

            AddChip(tag, selected: true);
            NewTagBox.Text = "";
            NewTagBox.Focus();
            UpdateSummary();
        }
        finally
        {
            AddTagBtn.IsEnabled = true;
        }
    }

    private void NewTagBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            AddTagBtn_Click(sender, new RoutedEventArgs());
    }

    private void UpdateSummary()
    {
        var selected = _chips.Where(c => c.IsSelected).ToList();
        SummaryText.Text = selected.Count == 0
            ? "No tags selected — import will proceed without tags."
            : $"{selected.Count} tag{(selected.Count == 1 ? "" : "s")} will be applied: {string.Join(", ", selected.Select(c => c.Tag.Name))}";
    }

    private void ApplyBtn_Click(object sender, RoutedEventArgs e)
        => DialogResult = true;

    private void SkipBtn_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;

    private sealed class TagChipState(Tag tag)
    {
        public Tag Tag { get; } = tag;
        public bool IsSelected { get; set; }
    }
}
