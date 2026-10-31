using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.Domain.Content;

namespace Launcher.App.ViewModels;

public sealed partial class DownloadViewModel
{
    private CancellationTokenSource? _packGalleryCts;
    [ObservableProperty] private byte[]? _packCover;
    [ObservableProperty] private string _packDescription = "";
    [ObservableProperty] private bool _packOverview;
    public bool IsPackResourceDetail => IsResourceDetail && OpenedResource?.Category == DownloadCategory.Modpack;
    public bool ShowsStandardResourceHeader => !IsPackResourceDetail;
    public bool ShowsResourceVersions => IsResourceDetail && (!IsPackResourceDetail || !PackOverview);
    public bool ShowsPackOverview => IsPackResourceDetail && PackOverview;
    public string PackAuthor => "作者 · " + (OpenedResource?.Author ?? "未知作者");
    public string PackSupportedVersions => OpenedResource?.VersionsText ?? "";
    public string PackLoaders => OpenedResource?.LoadersText ?? "";
    public string PackActivity => OpenedResource?.ActivityText ?? "";
    public bool HasPackCover => PackCover is { Length: > 0 };
    partial void OnPackCoverChanged(byte[]? value) => OnPropertyChanged(nameof(HasPackCover));
    partial void OnPackOverviewChanged(bool value) => NotifyPackPresentation();
    private void NotifyPackPresentation()
    {
        foreach (var name in new[] { nameof(IsPackResourceDetail), nameof(ShowsStandardResourceHeader), nameof(ShowsResourceVersions), nameof(ShowsPackOverview), nameof(PackAuthor), nameof(PackSupportedVersions), nameof(PackActivity), nameof(PackLoaders) }) OnPropertyChanged(name);
    }
    [RelayCommand] private void ShowPackOverview() => PackOverview = true;
    [RelayCommand] private void ShowPackVersions() => PackOverview = false;
    private async Task LoadPackPresentationAsync(DownloadItem item)
    {
        if (item.Category != DownloadCategory.Modpack) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)); _packGalleryCts = cts;
        PackDescription = item.DescriptionText;
        try
        {
            if (item.Source != ModrinthSource) return;
            var project = await _modrinth.GetProjectAsync(item.Id, cts.Token);
            if (OpenedResource != item || cts.IsCancellationRequested || project is null) return;
            if (project.Body is { Length: > 0 } body)
            {
                // Present a short readable overview without executing remote HTML or embedding a browser.
                body = Regex.Replace(body, @"<[^>]+>", "");
                body = Regex.Replace(body, @"!\[[^\]]*\]\([^)]*\)", "");
                body = Regex.Replace(body, @"\[\s*\]\([^)]*\)", "");
                body = Regex.Replace(body, @"\[([^\]]+)\]\([^)]*\)", "$1");
                body = Regex.Replace(body, @"(?m)^\s*\[[^\]\r\n]+\]:[^\r\n]*(?:\r?\n|$)", "");
                body = Regex.Replace(body, @"\[([^\]]+)\]\[[^\]]*\]", "$1");
                body = Regex.Replace(body, @"\[(?:\d+)\]", "");
                body = Regex.Replace(body, @"(?m)^\s{0,3}#{1,6}\s+", "");
                body = System.Net.WebUtility.HtmlDecode(Regex.Replace(body, @"\n{3,}", "\n\n").Replace("**", "")).Trim();
                PackDescription = body.Length > 1800 ? body[..1800] + "…" : body;
            }
            var image = project.Gallery?.OrderByDescending(g => g.Featured).FirstOrDefault();
            if (image is not null)
            {
                var bytes = await _presentation.GetIconAsync(image.Url, cts.Token);
                if (OpenedResource == item && !cts.IsCancellationRequested) PackCover = bytes;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* Artwork failure must not prevent version selection. */ }
        finally { if (ReferenceEquals(_packGalleryCts, cts)) _packGalleryCts = null; }
    }
}
