using Microsoft.Extensions.DependencyInjection;
using PhotoAlbum.App.Services;
using PhotoAlbum.Core.Domain;
using PhotoAlbum.Core.Interfaces;
using System.Windows;
using System.Windows.Controls;

namespace PhotoAlbum.App.Views;

public partial class DuplicatesView : Page
{
    public DuplicatesView()
    {
        InitializeComponent();
    }

    private async void ScanBtn_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current is not App app || app.Services is not { } sp) return;

        var pHashSvc  = sp.GetRequiredService<PHashService>();
        var dupSvc    = sp.GetRequiredService<DuplicateFinderService>();
        var mediaRepo = sp.GetRequiredService<IMediaItemRepository>();

        ScanProgress.Visibility = Visibility.Visible;
        InfoBar.Visibility      = Visibility.Collapsed;
        ScanBtn.IsEnabled       = false;
        EmptyText.Visibility    = Visibility.Collapsed;
        GroupList.ItemsSource   = null;

        try
        {
            // Fill any missing pHashes first
            await pHashSvc.FillAllAsync(ct: default);
            var groups = await dupSvc.FindDuplicatesAsync();

            if (groups.Count == 0)
            {
                EmptyText.Text       = "No duplicate groups found. Your library looks clean.";
                EmptyText.Visibility = Visibility.Visible;
                GroupCountText.Text  = "";
                return;
            }

            // Build display VMs
            var vms = new List<DuplicateGroupVm>();
            foreach (var group in groups)
            {
                var members = new List<DuplicateMemberVm>();
                foreach (var id in group.Ids)
                {
                    var item = await mediaRepo.GetByIdAsync(id);
                    if (item is not null)
                        members.Add(new DuplicateMemberVm(item));
                }
                if (members.Count > 1)
                    vms.Add(new DuplicateGroupVm(group, members));
            }

            GroupList.ItemsSource = vms;

            var total = vms.Sum(g => g.Members.Count);
            GroupCountText.Text = $"({vms.Count} group{(vms.Count == 1 ? "" : "s")}, {total} photos)";
            ShowInfo($"Found {vms.Count} duplicate group{(vms.Count == 1 ? "" : "s")}. " +
                     "Review each group and keep the photo you want.");
        }
        finally
        {
            ScanProgress.Visibility = Visibility.Collapsed;
            ScanBtn.IsEnabled       = true;
        }
    }

    private async void KeepThisBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not DuplicateMemberVm member) return;

        // Find the parent group VM
        var groupVm = FindParentGroup(member);
        if (groupVm is null) return;

        // Trash everything in the group except the kept item
        foreach (var m in groupVm.Members.Where(m => m.Id != member.Id))
            await TrashAsync(m.Id);

        RemoveGroup(groupVm);
    }

    private async void KeepBestBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not DuplicateGroupVm groupVm) return;

        // Keep the oldest (earliest CaptureUtc) and trash the rest
        var keeper = groupVm.Members
            .OrderBy(m => m.CaptureUtc ?? DateTime.MaxValue)
            .First();

        foreach (var m in groupVm.Members.Where(m => m.Id != keeper.Id))
            await TrashAsync(m.Id);

        RemoveGroup(groupVm);
    }

    private static async Task TrashAsync(long id)
    {
        if (Application.Current is not App app || app.Services is not { } sp) return;
        var mediaRepo = sp.GetRequiredService<IMediaItemRepository>();
        await mediaRepo.SoftDeleteAsync(id, DeleteMode.Trash);
    }

    private DuplicateGroupVm? FindParentGroup(DuplicateMemberVm member)
    {
        if (GroupList.ItemsSource is not List<DuplicateGroupVm> list) return null;
        return list.FirstOrDefault(g => g.Members.Any(m => m.Id == member.Id));
    }

    private void RemoveGroup(DuplicateGroupVm groupVm)
    {
        if (GroupList.ItemsSource is not List<DuplicateGroupVm> list) return;
        list.Remove(groupVm);
        GroupList.ItemsSource = null;
        GroupList.ItemsSource = list;

        GroupCountText.Text = list.Count > 0
            ? $"({list.Count} group{(list.Count == 1 ? "" : "s")}, {list.Sum(g => g.Members.Count)} photos)"
            : "";

        if (list.Count == 0)
        {
            EmptyText.Text       = "All groups resolved. Library is clean.";
            EmptyText.Visibility = Visibility.Visible;
            InfoBar.Visibility   = Visibility.Collapsed;
        }
    }

    private void ShowInfo(string message)
    {
        InfoBarText.Text    = message;
        InfoBar.Visibility  = Visibility.Visible;
    }
}

internal sealed class DuplicateGroupVm(DuplicateGroup group, List<DuplicateMemberVm> members)
{
    public DuplicateGroup           Group   { get; } = group;
    public IReadOnlyList<DuplicateMemberVm> Members { get; } = members;

    public string GroupLabel     { get; } = $"{members.Count} near-identical photos";
    public string SimilarityLabel { get; } =
        $"{(int)Math.Round((1.0 - group.MaxDistance / 64.0) * 100)}% similar";
}

internal sealed class DuplicateMemberVm(MediaItem item)
{
    public long      Id            { get; } = item.Id;
    public string    Name          { get; } = item.OriginalName;
    public string?   ThumbnailPath { get; } = item.ThumbnailPath;
    public DateTime? CaptureUtc   { get; } = item.CaptureUtc;
    public string    DateLabel     { get; } = item.CaptureUtc.HasValue
        ? item.CaptureUtc.Value.ToLocalTime().ToString("yyyy-MM-dd")
        : "Date unknown";
}
