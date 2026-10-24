using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.Application.Mods;
using Launcher.Application.Diagnostics;
using Launcher.Domain.Content;
using Microsoft.Extensions.Logging;

namespace Launcher.App.ViewModels;

public sealed record ResourceSortOption(ResourceSort Value, string Name)
{
    public override string ToString() => Name;
}

public sealed partial class DownloadViewModel
{
    private const int PageSize = 20;
    private int _browserGeneration;
    private bool _searching;
    private ResourceSearchRequest? _lastRequest;
    private ResourceSearchRequest? _installRequest;
    private string? _taxonomyKey;
    private bool _searchVersionsLoaded;
    public ObservableCollection<string> GameVersions { get; } = ["全部"];
    public ObservableCollection<string> Loaders { get; } = ["全部", "fabric", "forge", "neoforge", "quilt", "liteloader"];
    public ObservableCollection<ResourceTag> ResourceTags { get; } = [new("", "全部")];
    public ObservableCollection<ResourceSortOption> SortOptions { get; } =
    [ new(ResourceSort.Relevance, "相关度"), new(ResourceSort.Downloads, "下载量最大"),
      new(ResourceSort.Popularity, "热度最高"), new(ResourceSort.Updated, "最近更新"), new(ResourceSort.Newest, "最新发布") ];
    [ObservableProperty] private string _gameVersionFilter = "全部";
    [ObservableProperty] private string _loaderFilter = "全部";
    [ObservableProperty] private ResourceTag? _selectedTag;
    [ObservableProperty] private ResourceSortOption? _selectedSort = new(ResourceSort.Popularity, "热度最高");
    [ObservableProperty] private int _pageNumber = 1;
    [ObservableProperty] private long _totalResults;
    [ObservableProperty] private string _resultHeading = "热门资源推荐";
    public bool CanEditSearch => !IsBusy;
    public bool ShowsLoaderFilter => SelectedCategory?.Category.UsesLoader() == true;
    // CurseForge explicitly limits search to the first 10,000 results.
    private long ReachableResults => SelectedSource == CurseForgeSource ? Math.Min(10000, TotalResults) : TotalResults;
    public int TotalPages => (int)Math.Max(1, (ReachableResults + PageSize - 1) / PageSize);
    public string PageText => $"{PageNumber} / {TotalPages} 页 · {TotalResults:N0} 项" +
        (TotalResults > ReachableResults ? "（可浏览前 10,000 项，请缩小筛选）" : "");
    public string SortHint => SelectedSource == ModrinthSource ? "热度按关注数排序" : "热度按平台人气排序；相关度采用平台默认排序";
    private static string? FilterValue(string? value) => string.IsNullOrWhiteSpace(value) || value.Trim() == "全部" ? null : value.Trim();

    private void ResetBrowserState()
    {
        CloseResourceDetails(); _resourceExtrasCts?.Cancel();
        _browserGeneration++;
        if (_searching) _runningCts?.Cancel();
        _taxonomyKey = null;
        ResourceTags.Clear(); ResourceTags.Add(new("", "全部")); SelectedTag = ResourceTags[0];
        Results.Clear(); SelectedResult = null; TotalResults = 0; PageNumber = 1; _lastRequest = null; _installRequest = null;
        OnPropertyChanged(nameof(ShowsLoaderFilter)); OnPropertyChanged(nameof(SortHint));
    }
    partial void OnQueryChanged(string value) => ClearSearchResults();
    partial void OnGameVersionFilterChanged(string value) => ClearSearchResults();
    partial void OnLoaderFilterChanged(string value) => ClearSearchResults();
    partial void OnSelectedSortChanged(ResourceSortOption? value) => ClearSearchResults();
    partial void OnSelectedTagChanged(ResourceTag? oldValue, ResourceTag? newValue)
    {
        if (FilterValue(oldValue?.Id) != FilterValue(newValue?.Id)) ClearSearchResults();
    }
    private void ClearSearchResults()
    {
        CloseResourceDetails(); _resourceExtrasCts?.Cancel();
        Results.Clear(); SelectedResult = null; PageNumber = 1; TotalResults = 0;
        _lastRequest = null; _installRequest = null;
    }
    partial void OnPageNumberChanged(int value) => NotifyPaging();
    partial void OnTotalResultsChanged(long value) => NotifyPaging();
    private void NotifyPaging()
    {
        OnPropertyChanged(nameof(PageText)); OnPropertyChanged(nameof(TotalPages));
        FirstPageCommand.NotifyCanExecuteChanged(); PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged(); LastPageCommand.NotifyCanExecuteChanged();
        PopularCommand.NotifyCanExecuteChanged(); ResetFiltersCommand.NotifyCanExecuteChanged();
    }
    private bool CanPreviousPage() => !IsBusy && PageNumber > 1 && _lastRequest is not null;
    private bool CanNextPage() => !IsBusy && PageNumber < TotalPages && _lastRequest is not null;
    [RelayCommand(CanExecute = nameof(CanPreviousPage))] private Task FirstPageAsync() => ChangePageAsync(1);
    [RelayCommand(CanExecute = nameof(CanPreviousPage))] private Task PreviousPageAsync() => ChangePageAsync(PageNumber - 1);
    [RelayCommand(CanExecute = nameof(CanNextPage))] private Task NextPageAsync() => ChangePageAsync(PageNumber + 1);
    [RelayCommand(CanExecute = nameof(CanNextPage))] private Task LastPageAsync() => ChangePageAsync(TotalPages);
    private Task ChangePageAsync(int page)
    {
        PageNumber = Math.Clamp(page, 1, TotalPages);
        return BrowseAsync(paging: true);
    }
    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task PopularAsync()
    {
        Query = ""; SelectedSort = SortOptions.Single(s => s.Value == ResourceSort.Popularity);
        PageNumber = 1; await BrowseAsync();
    }
    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task ResetFiltersAsync()
    {
        Query = ""; GameVersionFilter = "全部"; LoaderFilter = "全部"; SelectedTag = ResourceTags.FirstOrDefault();
        SelectedSort = SortOptions.Single(s => s.Value == ResourceSort.Popularity);
        PageNumber = 1; await BrowseAsync();
    }
    private async Task BrowseAsync(CancellationToken ct = default, bool paging = false)
    {
        if (IsBusy || !IsSearchMode || SelectedCategory is null) return;
        var category = SelectedCategory.Category; var source = SelectedSource; var generation = _browserGeneration;
        var request = paging && _lastRequest is not null ? _lastRequest with { Offset = (PageNumber - 1) * PageSize } :
            new ResourceSearchRequest(ResourceChinese.SearchQuery(Query.Trim()), category, FilterValue(GameVersionFilter),
                category.UsesLoader() ? FilterValue(LoaderFilter) : null, FilterValue(SelectedTag?.Id),
                SelectedSort?.Value ?? ResourceSort.Popularity, PageSize, (PageNumber - 1) * PageSize);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _runningCts = cts; _searching = true; IsBusy = true;
        Results.Clear(); SelectedResult = null; _lastRequest = null; _installRequest = null; Status = $"正在从 {source} 获取资源…";
        ResultHeading = string.IsNullOrWhiteSpace(request.Query) && request.Sort == ResourceSort.Popularity ? "热门资源推荐" : "检索结果";
        try
        {
            if (source == CurseForgeSource && !_curseForge.IsConfigured)
            { TotalResults = 0; Status = "CurseForge 尚未配置 API Key；可在设置中配置，或切换到 Modrinth。"; return; }
            // Metadata is loaded only when a resource page is visited. Search failure must not hide filters.
            _ = LoadSearchMetadataSafelyAsync(source, category, generation, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (source == CurseForgeSource)
            {
                try
                {
                    var page = await _curseForge.SearchAsync(request, cts.Token);
                    cts.Token.ThrowIfCancellationRequested();
                    if (generation != _browserGeneration) return;
                    foreach (var mod in page.Data.Where(m => m.ClassId == category.CurseForgeClassId()))
                        Results.Add(CurseForgeItem(mod,category));
                    TotalResults = page.TotalCount;
                }
                catch (CurseForgeSearchUnavailableException)
                {
                    _lastRequest = null; TotalResults = 0; PageNumber = 1;
                    await SearchCurseForgeByIdAsync(category, cts.Token);
                    cts.Token.ThrowIfCancellationRequested();
                    if (generation != _browserGeneration) { Results.Clear(); return; }
                    TotalResults = Results.Count; _installRequest = request;
                    _ = EnrichResultsAsync(Results.ToArray());
                    return;
                }
            }
            else
            {
                var response = await _modrinth.SearchAsync(request, cts.Token);
                cts.Token.ThrowIfCancellationRequested();
                if (generation != _browserGeneration) return;
                foreach (var hit in response.Hits.Where(h => MatchesCategory(h, category)))
                    Results.Add(ModrinthItem(hit,category));
                TotalResults = response.TotalHits;
            }
            _ = EnrichResultsAsync(Results.ToArray());
            _lastRequest = request; _installRequest = request;
            Status = Results.Count == 0 ? "没有匹配资源，可调整版本、分类或关键词。" : $"{source} · {ResultHeading} · 当前 {Results.Count} 项";
        }
        catch (OperationCanceledException)
        {
            if (generation == _browserGeneration)
            { _lastRequest = null; TotalResults = 0; PageNumber = 1; Status = "已取消"; }
        }
        catch (Exception ex)
        {
            if (generation == _browserGeneration) { TotalResults = 0; _lastRequest = null; Status = "检索失败：" + ErrorText.Describe(ex); }
            _logger.LogWarning(ex, "资源检索失败");
        }
        finally
        {
            _searching = false; if (ReferenceEquals(_runningCts, cts)) _runningCts = null;
            IsBusy = false; NotifyPaging();
            if (generation != _browserGeneration && IsSearchMode && _autoLoadEnabled) _ = BrowseAsync();
        }
    }
    private async Task LoadSearchMetadataSafelyAsync(string source, DownloadCategory category, int generation, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { await LoadSearchMetadataAsync(source, category, generation, timeout.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogDebug(ex, "筛选元数据加载失败，仍可搜索"); }
    }
    private async Task LoadSearchMetadataAsync(string source, DownloadCategory category, int generation, CancellationToken ct)
    {
        var key = source + category;
        if (_taxonomyKey != key)
        {
            try
            {
                var tags = source == CurseForgeSource ? await _curseForge.GetCategoriesAsync(category, ct) : await _modrinth.GetCategoriesAsync(category, ct);
                ct.ThrowIfCancellationRequested();
                if (generation != _browserGeneration || SelectedSource != source || SelectedCategory?.Category != category) return;
                var selected = SelectedTag?.Id;
                ResourceTags.Clear(); ResourceTags.Add(new("", "全部"));
                foreach (var tag in tags) ResourceTags.Add(tag);
                SelectedTag = ResourceTags.FirstOrDefault(t => t.Id == selected) ?? ResourceTags[0];
                _taxonomyKey = key;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogDebug(ex, "分类列表暂不可用"); }
        }
        if (!_searchVersionsLoaded)
        {
            try
            {
                var manifest = await _meta.GetManifestAsync(ct);
                ct.ThrowIfCancellationRequested();
                if (generation != _browserGeneration) return;
                GameVersions.Clear(); GameVersions.Add("全部");
                foreach (var version in manifest.Versions.Where(v => v.Type == "release").Select(v => v.Id).Distinct()) GameVersions.Add(version);
                _searchVersionsLoaded = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogDebug(ex, "版本列表暂不可用，仍可手动输入"); }
        }
    }
    private static string DateText(DateTimeOffset? value) => value?.ToLocalTime().ToString("yyyy-MM-dd") ?? "未知";
    private static string VersionsText(System.Collections.Generic.IEnumerable<string> versions)
    {
        var list = versions.Distinct().ToArray();
        return list.Length == 0 ? "版本未标注" : string.Join(", ", list.TakeLast(3)) + (list.Length > 3 ? " 等" : "");
    }
    private static bool MatchesCategory(ModrinthProject project, DownloadCategory category) =>
        category == DownloadCategory.Datapack ? project.AllProjectTypes?.Contains("datapack") == true || project.ProjectType == "datapack" || project.Categories.Contains("datapack") :
        project.ProjectType is null || project.ProjectType == category.ModrinthProjectType();
}
