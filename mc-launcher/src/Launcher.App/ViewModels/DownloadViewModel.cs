using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.Application.Abstractions;
using Launcher.Application.Diagnostics;
using Launcher.Application.Install;
using Launcher.Application.Mods;
using Launcher.Application.Settings;
using Launcher.Domain.Content;
using Launcher.Domain.Versions;
using Microsoft.Extensions.Logging;

namespace Launcher.App.ViewModels;

/// <summary>左侧下载导航里的一项。IsSeparator 为真的那项只画一条分隔线（游戏本体+模组 | 四个包）。</summary>
public sealed partial class CategoryItem : ObservableObject
{
    public CategoryItem(DownloadCategory category, string title, string hint, bool isSeparator = false)
    {
        Category = category;
        Title = title;
        Hint = hint;
        IsSeparator = isSeparator;
    }

    public DownloadCategory Category { get; }
    public string Title { get; }
    public string Hint { get; }
    public bool IsSeparator { get; }
    public bool IsNormal => !IsSeparator;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>点击切换分类。构造时注入，省得 XAML 跨层找命令。</summary>
    public ICommand? SelectCommand { get; init; }

    public override string ToString() => Title;
}

/// <summary>搜索结果或版本列表里的一行。</summary>
public sealed record DownloadItem(
    string Source,
    DownloadCategory Category,
    string Id,
    string Title,
    string Author,
    string MetaText,
    string Description)
{
    public string Badge => Source == DownloadViewModel.CurseForgeSource ? "CF" : "MR";

    public override string ToString() => $"{Title}（{Badge}）";
}

/// <summary>Minecraft 原版版本条目。</summary>
public sealed partial class VersionItem(string id, string type, DateTimeOffset releaseTime) : ObservableObject
{
    public string Id { get; } = id;
    public string Type { get; } = type;
    public DateTimeOffset ReleaseTime { get; } = releaseTime;

    public bool IsRelease => Type == "release";

    public string ReleaseDateText => ReleaseTime.ToLocalTime().ToString("yyyy/MM/dd HH:mm");

    public string TypeText => Type switch
    {
        "release" => "正式版",
        "snapshot" => "快照",
        "old_beta" => "远古 Beta",
        "old_alpha" => "远古 Alpha",
        _ => Type,
    };

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>点击选中。由 VM 在创建时注入，省得 XAML 里跨三层找命令。</summary>
    public ICommand? SelectCommand { get; init; }

    public override string ToString() => $"{Id}（{TypeText}）";
}

/// <summary>版本抽屉：按版本类型分组（正式版 / 预览版 / 远古版），组里平铺完整版本号。</summary>
public sealed partial class VersionGroup(string key) : ObservableObject
{
    public string Key { get; } = key;
    public ObservableCollection<VersionItem> Versions { get; } = [];

    [ObservableProperty]
    private bool _isExpanded;

    public int Count => Versions.Count;
    public string Header => $"{Key}（{Count} 个版本）";
}

/// <summary>
/// 下载中心：左侧按资源类型分组，右侧搜索并安装到当前版本。
/// 「游戏本体」是唯一不依赖版本的分组 —— 它安装的是新的游戏本体。
/// </summary>
public sealed partial class DownloadViewModel : ObservableObject
{
    public const string ModrinthSource = "Modrinth";
    public const string CurseForgeSource = "CurseForge";

    private readonly IModrinthClient _modrinth;
    private readonly ICurseForgeClient _curseForge;
    private readonly ModInstallService _installer;
    private readonly CurseForgeInstallService _cfInstaller;
    private readonly ModpackDownloadService _modpackDownloader;
    private readonly GameInstaller _gameInstaller;
    private readonly IVersionMetaClient _meta;
    private readonly ILogger<DownloadViewModel> _logger;
    private readonly Func<string> _gameRoot;
    private readonly Func<string?> _currentInstance;

    private bool _versionsLoaded;
    private List<VersionItem> _allVersions = [];

    /// <summary>当前耗时命令的取消源。只在真正干活期间存在，命令返回前一定清掉。</summary>
    private CancellationTokenSource? _runningCts;

    public DownloadViewModel(
        IModrinthClient modrinth,
        ICurseForgeClient curseForge,
        ModInstallService installer,
        CurseForgeInstallService cfInstaller,
        ModpackDownloadService modpackDownloader,
        GameInstaller gameInstaller,
        IVersionMetaClient meta,
        ILogger<DownloadViewModel> logger,
        Func<string> gameRoot,
        Func<string?> currentInstance)
    {
        _modrinth = modrinth;
        _curseForge = curseForge;
        _installer = installer;
        _cfInstaller = cfInstaller;
        _modpackDownloader = modpackDownloader;
        _gameInstaller = gameInstaller;
        _meta = meta;
        _logger = logger;
        _gameRoot = gameRoot;
        _currentInstance = currentInstance;

        // 上两块：游戏本体 + 模组；中间一条线；下面四块是资源包类内容
        Categories =
        [
            new(DownloadCategory.MinecraftVersion, "游戏本体", "原版各版本，装好即可启动") { SelectCommand = SelectCategoryCommand },
            new(DownloadCategory.Mod, "模组", "需要适配版本的加载器") { SelectCommand = SelectCategoryCommand },
            new(DownloadCategory.Modpack, string.Empty, string.Empty, isSeparator: true) { SelectCommand = SelectCategoryCommand },
            new(DownloadCategory.Modpack, "整合包", "会新建一个版本") { SelectCommand = SelectCategoryCommand },
            new(DownloadCategory.ResourcePack, "资源包", "与加载器无关") { SelectCommand = SelectCategoryCommand },
            new(DownloadCategory.Datapack, "数据包", "与加载器无关") { SelectCommand = SelectCategoryCommand },
            new(DownloadCategory.Shader, "光影包", "需要 Oculus / Iris") { SelectCommand = SelectCategoryCommand },
        ];

        SelectedCategory = Categories[0];
        Categories[0].IsSelected = true;
    }

    /// <summary>装完版本或整合包后通知主界面刷新版本列表。</summary>
    public event Action? InstancesChanged;

    /// <summary>主界面切到下载页时调用：当前版本可能刚变过，重算一下落盘目录提示。</summary>
    public void RefreshTarget() => TargetText = BuildTargetText(SelectedCategory?.Category);

    /// <summary>
    /// 首次进入下载页时才真正拉版本清单（920 条，几百毫秒 + 一次网络请求）。
    /// 构造函数只摆好分类骨架，避免和首帧抢时间。
    /// </summary>
    public Task EnsureLoadedAsync()
    {
        _autoLoadEnabled = true;
        return IsVersionMode ? EnsureVersionsAsync() : Task.CompletedTask;
    }

    public ObservableCollection<CategoryItem> Categories { get; }

    public ObservableCollection<DownloadItem> Results { get; } = [];

    public ObservableCollection<VersionGroup> VersionGroups { get; } = [];

    /// <summary>顶部「最新版本」卡片：最新正式版 + 最新预览版（跟 PCL 一样放最上面）。</summary>
    public ObservableCollection<VersionItem> LatestVersions { get; } = [];

    public ObservableCollection<string> Sources { get; } = [ModrinthSource, CurseForgeSource];

    [ObservableProperty]
    private CategoryItem? _selectedCategory;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private string _selectedSource = ModrinthSource;

    /// <summary>
    /// 下载源（官方 / BMCLAPI）。存在 settings.json 里，是全局设置，不是版本级。
    /// 下载器每次改写 URL 前会 stat 一次 settings.json，所以这里写完立刻生效。
    /// </summary>
    [ObservableProperty]
    private DownloadSource _downloadSource = DownloadSourceStore.Read(AppPaths.DataDirectory);

    /// <summary>构造函数阶段为 false：只摆骨架，不拉清单（首帧优先）。见 EnsureLoadedAsync。</summary>
    private bool _autoLoadEnabled;

    public bool IsOfficialSource => DownloadSource == DownloadSource.Official;

    public bool IsBmclapiSource => DownloadSource == DownloadSource.Bmclapi;

    partial void OnDownloadSourceChanged(DownloadSource value)
    {
        OnPropertyChanged(nameof(IsOfficialSource));
        OnPropertyChanged(nameof(IsBmclapiSource));
    }

    /// <summary>切换下载源。只影响 Mojang / Fabric / Forge 那条线，Modrinth 与 CurseForge 各有自己的地址。</summary>
    [RelayCommand]
    private void UseDownloadSource(string? token)
    {
        if (!DownloadSourceText.TryParse(token, out var source) || source == DownloadSource) return;

        try
        {
            DownloadSourceStore.Write(source, AppPaths.DataDirectory);
            DownloadSource = source;
            Status = source == DownloadSource.Bmclapi                ? "已切到 BMCLAPI 镜像：游戏本体、libraries、assets、Fabric meta 走镜像；Modrinth / CurseForge 不受影响"
                : "已切回官方源";
        }
        catch (Exception ex)
        {
            Status = "切换下载源失败：" + ErrorText.Describe(ex);
        }
    }

    [ObservableProperty]
    private DownloadItem? _selectedResult;

    [ObservableProperty]
    private VersionItem? _selectedVersion;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>有任务在跑时才显示「取消」按钮。</summary>
    [ObservableProperty]
    private bool _canCancel;

    [ObservableProperty]
    private string _status = "选择一个分类开始";

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private bool _releasesOnly = true;

    [ObservableProperty]
    private string _targetText = string.Empty;

    /// <summary>当前分组是不是「游戏本体」。</summary>
    public bool IsVersionMode => SelectedCategory?.Category == DownloadCategory.MinecraftVersion;

    /// <summary>不是版本分组时才有搜索框。</summary>
    public bool IsSearchMode => !IsVersionMode;

    /// <summary>只有联机资源库才需要选源。</summary>
    public bool ShowsSourcePicker => IsSearchMode;

    /// <summary>Key 没配时才提示怎么申请；配好了就不再打扰。</summary>
    public bool ShowsCurseForgeHint =>
        IsSearchMode && SelectedSource == CurseForgeSource && !_curseForge.IsConfigured;

    public bool ShowsReleasesOnly => IsVersionMode;

    public string SearchHint => SelectedSource == CurseForgeSource
        ? "输入 CurseForge 项目 ID，例如 238222"
        : SelectedCategory?.Category switch
        {
            DownloadCategory.Mod => "搜索模组，例如 jei、sodium",
            DownloadCategory.Modpack => "搜索整合包，例如 通天之路、Better MC",
            DownloadCategory.ResourcePack => "搜索资源包，例如 Faithful",
            DownloadCategory.Datapack => "搜索数据包，例如 Terralith",
            DownloadCategory.Shader => "搜索光影包，例如 Complementary",
            _ => "输入关键词",
        };

    partial void OnSelectedCategoryChanged(CategoryItem? value)
    {
        OnPropertyChanged(nameof(IsVersionMode));
        OnPropertyChanged(nameof(IsSearchMode));
        OnPropertyChanged(nameof(ShowsSourcePicker));
        OnPropertyChanged(nameof(ShowsCurseForgeHint));
        OnPropertyChanged(nameof(ShowsReleasesOnly));
        OnPropertyChanged(nameof(SearchHint));

        // 左栏是自己画的按钮（不是 ListBox），选中态得手动同步
        foreach (var category in Categories)
            category.IsSelected = ReferenceEquals(category, value);

        Results.Clear();
        SelectedResult = null;
        TargetText = BuildTargetText(value?.Category);

        // RelayCommand 不挂 CommandManager，切换分类后必须自己重问一次 CanExecute，
        // 否则「搜索」按钮会停在启动时的禁用态。
        SearchCommand.NotifyCanExecuteChanged();
        InstallCommand.NotifyCanExecuteChanged();

        if (_autoLoadEnabled && value?.Category == DownloadCategory.MinecraftVersion)
        {
            _ = EnsureVersionsAsync();
        }
        else
        {
            Status = value is null ? string.Empty : $"{value.Title}：{value.Hint}";
        }
    }

    [RelayCommand]
    private void SelectCategory(CategoryItem? item)
    {
        if (item is null || item.IsSeparator) return;
        SelectedCategory = item;
    }

    partial void OnSelectedSourceChanged(string value)
    {
        OnPropertyChanged(nameof(ShowsCurseForgeHint));
        OnPropertyChanged(nameof(SearchHint));
        Results.Clear();
        SelectedResult = null;
        Status = value == CurseForgeSource
            ? _curseForge.IsConfigured
                ? "CurseForge：输入项目 ID（例如 238222）后回车"
                : "CurseForge 需要 API Key（见上方说明）"
            : $"{value}：输入关键词后回车";
    }

    /// <summary>界面上的「Modrinth / CurseForge」切换按钮。</summary>
    [RelayCommand]
    private void UseSource(string? source)
    {
        if (!string.IsNullOrWhiteSpace(source))
        {
            SelectedSource = source;
        }
    }

    partial void OnReleasesOnlyChanged(bool value) => ApplyVersionFilter();

    partial void OnIsBusyChanged(bool value)
    {
        SearchCommand.NotifyCanExecuteChanged();
        InstallCommand.NotifyCanExecuteChanged();
        InstallVersionCommand.NotifyCanExecuteChanged();
        CanCancel = value;
    }

    /// <summary>
    /// 取消当前耗时任务。只发信号：状态与 _runningCts 由命令自己的 finally 收尾，
    /// 免得这里提前把 IsBusy 放掉、命令却还在跑。
    /// </summary>
    [RelayCommand]
    private void Cancel()
    {
        if (_runningCts is not { } cts || cts.IsCancellationRequested) return;

        try
        {
            cts.Cancel();
            Status = "正在取消…";
            _logger.LogInformation("用户请求取消当前下载任务");
        }
        catch (Exception ex)
        {
            // 命令恰好收尾释放了 cts，或某个注册回调自己抛了；都不该让 UI 线程挂掉
            _logger.LogWarning(ex, "取消信号发出失败");
        }
    }

    /// <summary>取消常被 HttpClient / Task.WhenAll 包一层 AggregateException，剥到内层再判断。</summary>
    private static bool IsCancellation(Exception? ex) => ex switch
    {
        null => false,
        OperationCanceledException => true,
        AggregateException aggregate => aggregate.InnerExceptions.Any(IsCancellation),
        _ => IsCancellation(ex.InnerException),
    };

    partial void OnSelectedResultChanged(DownloadItem? value) => InstallCommand.NotifyCanExecuteChanged();

    partial void OnSelectedVersionChanged(VersionItem? value)
    {
        InstallVersionCommand.NotifyCanExecuteChanged();

        // 900 个条目全遍历一次，代价可忽略，但能保证高亮只落在一个上
        foreach (var item in _allVersions) item.IsSelected = ReferenceEquals(item, value);
    }

    private string BuildTargetText(DownloadCategory? category)
    {
        if (category is null) return string.Empty;

        if (category == DownloadCategory.MinecraftVersion)
            return $"游戏目录 {_gameRoot()}";

        if (category == DownloadCategory.Modpack)
            return "整合包会新建独立版本，不影响现有版本";

        var instance = _currentInstance();
        return instance is null
            ? "先在左侧选一个版本，才能装到它的目录里"
            : $"{instance} → .minecraft\\{category.Value.InstanceSubdirectory()}";
    }

    private bool CanSearch() => !IsBusy && IsSearchMode;

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchAsync(CancellationToken ct)
    {
        if (SelectedCategory is null) return;

        if (string.IsNullOrWhiteSpace(Query))
        {
            Status = "先输入关键词";
            return;
        }

        IsBusy = true;
        ProgressPercent = 0;
        Results.Clear();
        SelectedResult = null;

        // 真正开始请求前才建取消源，和命令传进来的 token 串起来
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _runningCts = cts;

        try
        {
            var category = SelectedCategory.Category;
            var gameVersion = ResolveGameVersion();
            var loader = ResolveLoader();

            Status = $"正在 {SelectedSource} 搜索「{Query}」…";

            if (SelectedSource == CurseForgeSource)
            {
                if (!_curseForge.IsConfigured)
                {
                    Status = "CurseForge 需要 API Key：到 console.curseforge.com 免费申请，填入设置后重启";
                    return;
                }

                try
                {
                    var page = await _curseForge.SearchAsync(Query, category, gameVersion, loader, 20, 0, cts.Token).ConfigureAwait(true);
                    foreach (var mod in page.Data)
                    {
                        Results.Add(new DownloadItem(
                            CurseForgeSource,
                            category,
                            mod.Id.ToString(),
                            mod.Name,
                            mod.Authors.FirstOrDefault()?.Name ?? "未知作者",
                            $"{mod.DownloadsText} 次下载",
                            mod.Summary ?? string.Empty));
                    }

                    Status = $"CurseForge 命中 {page.Pagination?.TotalCount ?? Results.Count} 个，已取 {Results.Count} 条";
                }
                catch (CurseForgeSearchUnavailableException)
                {
                    // 个人 Core Key 通常不给搜索权限，退回按项目 ID 直读
                    await SearchCurseForgeByIdAsync(category, cts.Token).ConfigureAwait(true);
                }
            }
            else
            {
                // 整合包自带 MC 版本和加载器，按当前版本的 MC 版本过滤会把绝大多数包筛掉
                var response = await _modrinth.SearchAsync(
                    Query,
                    category,
                    category == DownloadCategory.Modpack ? null : gameVersion,
                    loader,
                    ct: cts.Token).ConfigureAwait(true);

                foreach (var hit in response.Hits)
                {
                    Results.Add(new DownloadItem(
                        ModrinthSource,
                        category,
                        hit.Slug ?? hit.ProjectId ?? string.Empty,
                        hit.Title ?? string.Empty,
                        hit.Author ?? "未知作者",
                        $"{hit.DownloadsText} 次下载",
                        hit.Description ?? string.Empty));
                }

                Status = $"Modrinth 命中 {response.TotalHits} 个，已取 {Results.Count} 条";
            }
        }
        catch (Exception ex) when (IsCancellation(ex))
        {
            _logger.LogInformation(ex, "搜索已取消");
            Status = "已取消";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "搜索失败");
            Status = "搜索失败：" + ErrorText.Describe(ex);
        }
        finally
        {
            if (ReferenceEquals(_runningCts, cts)) _runningCts = null;
            IsBusy = false;
            ProgressPercent = 0;
        }
    }

    private bool CanInstall() => !IsBusy && SelectedResult is not null;

    /// <summary>
    /// CurseForge 搜索被拒时的退路：把输入当项目 ID（或链接里的数字 ID）直读。
    /// 官方 API 只有 <c>/v1/mods/&lt;id&gt;</c> 与 <c>/files</c> 对个人 Key 开放，
    /// 项目页 URL 里是 slug（如 /mc-mods/jei），所以这里只能接受数字。
    /// </summary>
    private async Task SearchCurseForgeByIdAsync(DownloadCategory category, CancellationToken ct)
    {
        var reference = _curseForge.ParseUrl(Query);
        int? modId = reference?.ModId;

        if (modId is null && int.TryParse(Query.Trim(), out var parsed))
        {
            modId = parsed;
        }

        if (modId is null)
        {
            Status = "这个 Key 不能调搜索端点。改为输入 CurseForge 项目 ID（项目页右侧「Project ID: 238222」那种数字）"
                + "，或 /files/9277600 这样的链接。";
            return;
        }

        Status = $"正在读取 CurseForge 项目 {modId}…";
        var mod = await _curseForge.GetModAsync(modId.Value, ct).ConfigureAwait(true);
        if (mod is null)
        {
            Status = $"CurseForge 上没有 id={modId} 的项目";
            return;
        }

        Results.Add(new DownloadItem(
            CurseForgeSource,
            category,
            mod.Id.ToString(),
            mod.Name,
            mod.Authors.FirstOrDefault()?.Name ?? "未知作者",
            $"{mod.DownloadsText} 次下载",
            mod.Summary ?? string.Empty));

        Status = $"CurseForge 直读：{mod.Name}（当前 Key 只支持按项目 ID 查询）";
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        using var operation = Launcher.Application.Operations.GameOperationCoordinator.Shared.TryEnter(_gameRoot(), "安装资源");
        if (operation is null) { Status = Launcher.Application.Operations.GameOperationCoordinator.Shared.BusyMessage(_gameRoot()); return; }
        if (SelectedResult is not { } item || SelectedCategory is null) return;

        var category = item.Category;

        if (category != DownloadCategory.Modpack && _currentInstance() is not { } instance)
        {
            Status = "先在左侧选一个版本";
            return;
        }

        IsBusy = true;
        ProgressPercent = 0;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        _runningCts = cts;

        try
        {
            var progress = new Progress<ModInstallProgress>(p =>
            {
                ProgressPercent = p.Percent;
                Status = string.IsNullOrEmpty(p.Detail) ? p.Stage : $"{p.Stage} · {p.Detail}";
            });

            if (category == DownloadCategory.Modpack)
            {
                await InstallModpackAsync(item, cts.Token);
                return;
            }

            var instanceId = _currentInstance()!;

            if (item.Source == CurseForgeSource)
            {
                if (!int.TryParse(item.Id, out var modId))
                {
                    Status = $"CurseForge 项目 ID 无法解析：{item.Id}";
                    return;
                }

                var cfReport = await _cfInstaller.InstallAsync(
                    _gameRoot(), instanceId, category, modId, progress, cts.Token);

                Status = $"「{cfReport.ProjectName}」已装到 {Path.GetFileName(cfReport.TargetDirectory)}" +
                         (cfReport.SkippedFiles > 0 ? $"（跳过 {cfReport.SkippedFiles} 个已存在）" : string.Empty);
                return;
            }

            var report = await _installer.InstallAsync(
                _gameRoot(), instanceId, category, item.Id, progress, cts.Token);

            Status = $"「{report.ProjectTitle} {report.VersionNumber}」已装到 {Path.GetFileName(report.TargetDirectory)}" +
                     (report.DependencyFiles > 0 ? $"，附带 {report.DependencyFiles} 个依赖" : string.Empty) +
                     (report.SkippedFiles > 0 ? $"，跳过 {report.SkippedFiles} 个已存在" : string.Empty);
        }
        catch (Exception ex) when (IsCancellation(ex))
        {
            _logger.LogInformation(ex, "安装已取消");
            Status = "已取消";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "安装失败");
            Status = "安装失败：" + ErrorText.Describe(ex);
        }
        finally
        {
            if (ReferenceEquals(_runningCts, cts)) _runningCts = null;
            IsBusy = false;
            ProgressPercent = 0;
        }
    }

    private async Task InstallModpackAsync(DownloadItem item, CancellationToken ct)
    {
        var progress = new Progress<ModpackImportProgress>(p =>
        {
            ProgressPercent = p.Percent;
            Status = string.IsNullOrEmpty(p.Detail) ? p.Stage : $"{p.Stage} · {p.Detail}";
        });

        ModpackImportReport report;

        if (item.Source == CurseForgeSource)
        {
            if (!int.TryParse(item.Id, out var cfModId))
            {
                Status = $"CurseForge 项目 ID 无法解析：{item.Id}";
                return;
            }

            report = await _modpackDownloader.InstallCurseForgeAsync(_gameRoot(), cfModId, progress, ct);
        }
        else
        {
            report = await _modpackDownloader.InstallAsync(_gameRoot(), item.Id, progress, ct);
        }

        Status = $"「{report.PackName} {report.PackVersionId}」导入完成 · {report.InstanceId} · 下载 {report.DownloadedFiles} / 跳过 {report.SkippedFiles}";
        InstancesChanged?.Invoke();
    }

    private bool CanInstallVersion() => !IsBusy && SelectedVersion is not null;

    /// <summary>版本列表里的叶子点击。</summary>
    [RelayCommand]
    private void SelectVersion(VersionItem? item)
    {
        if (item is not null) SelectedVersion = item;
    }

    /// <summary>PCL 式排版：正式版 / 预览版 / 远古版各成一组。</summary>
    private static string GroupNameFor(string type) => type switch
    {
        "release" => "正式版",
        "snapshot" => "预览版",
        "old_beta" or "old_alpha" => "远古版",
        _ => "其他",
    };

    [RelayCommand(CanExecute = nameof(CanInstallVersion))]
    private async Task InstallVersionAsync()
    {
        using var operation = Launcher.Application.Operations.GameOperationCoordinator.Shared.TryEnter(_gameRoot(), "安装游戏版本");
        if (operation is null) { Status = Launcher.Application.Operations.GameOperationCoordinator.Shared.BusyMessage(_gameRoot()); return; }
        if (SelectedVersion is not { } version) return;

        IsBusy = true;
        ProgressPercent = 0;
        Status = $"开始安装 {version.Id}…";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        _runningCts = cts;

        try
        {
            var progress = new Progress<DownloadProgress>(p =>
            {
                ProgressPercent = p.Percent;
                Status = $"{p.Percent:F0}%  {p.CompletedBytes / 1024.0 / 1024.0:F1} MB  {p.CurrentFile}";
            });

            var summary = await _gameInstaller.InstallAsync(version.Id, _gameRoot(), includeAssets: true, progress, cts.Token);
            Status = $"{version.Id} 安装完成 · 下载 {summary.DownloadedFiles} / 跳过 {summary.SkippedFiles}";
            InstancesChanged?.Invoke();
        }
        catch (Exception ex) when (IsCancellation(ex))
        {
            _logger.LogInformation(ex, "安装版本 {Version} 已取消", version.Id);
            Status = "已取消";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "安装版本 {Version} 失败", version.Id);
            Status = "安装失败：" + ErrorText.Describe(ex);
        }
        finally
        {
            if (ReferenceEquals(_runningCts, cts)) _runningCts = null;
            IsBusy = false;
            ProgressPercent = 0;
        }
    }

    private bool _allVersionsLoaded;

    private async Task EnsureVersionsAsync()
    {
        if (_versionsLoaded) return;

        Status = "正在拉取版本清单…";

        try
        {
            var manifest = await _meta.GetManifestAsync();
            _allVersions = manifest.Versions
                .Select(entry => new VersionItem(entry.Id, entry.Type, entry.ReleaseTime) { SelectCommand = SelectVersionCommand })
                .ToList();
            _allVersionsLoaded = true;
            _versionsLoaded = true;

            // 顶部卡片：最新正式版 + 最新预览版
            LatestVersions.Clear();
            if (_allVersions.FirstOrDefault(v => v.Type == "release") is { } latestRelease)
                LatestVersions.Add(latestRelease);
            if (_allVersions.FirstOrDefault(v => v.Type == "snapshot") is { } latestSnapshot)
                LatestVersions.Add(latestSnapshot);

            ApplyVersionFilter();
            Status = $"共 {_allVersions.Count} 个游戏版本，最新正式版 {manifest.Latest?.Release}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "拉取版本清单失败");
            Status = "拉取版本清单失败：" + ErrorText.Describe(ex);
        }
    }

    private void ApplyVersionFilter()
    {
        if (!_versionsLoaded || !_allVersionsLoaded) return;

        // PCL 式排版：按版本类型分组（正式版 / 预览版 / 远古版），组里平铺完整版本号。
        // 清单里类型是混排的（26.3 正式版夹着 26.4-snapshot-5），必须按固定顺序归组，
        // 不能顺序切，否则同类型会散成好几个同名组。
        VersionGroups.Clear();

        foreach (var type in new[] { "release", "snapshot", "old_beta", "old_alpha" })
        {
            var items = _allVersions.Where(v => v.Type == type).ToList();
            if (items.Count == 0) continue;

            var bucket = new VersionGroup(GroupNameFor(type)) { IsExpanded = VersionGroups.Count == 0 };
            foreach (var item in items) bucket.Versions.Add(item);
            VersionGroups.Add(bucket);
        }

        // 万一以后 Mojang 加了新类型，别把版本吞掉
        var known = new[] { "release", "snapshot", "old_beta", "old_alpha" };
        var rest = _allVersions.Where(v => !known.Contains(v.Type)).ToList();
        if (rest.Count > 0)
        {
            var other = new VersionGroup("其他");
            foreach (var item in rest) other.Versions.Add(item);
            VersionGroups.Add(other);
        }

        // 选中的版本被清空后（重新拉清单）要把选择也清掉，否则「下载并安装」会指向列表里看不到的东西
        if (SelectedVersion is not null && !_allVersions.Contains(SelectedVersion))
            SelectedVersion = null;
    }

    private string? ResolveGameVersion()
    {
        if (SelectedCategory?.Category == DownloadCategory.MinecraftVersion) return null;
        if (_currentInstance() is not { } instanceId) return null;

        try
        {
            return InstanceEnvironment.Resolve(_gameRoot(), instanceId).GameVersion;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private string? ResolveLoader()
    {
        if (SelectedCategory is not { } category || !category.Category.UsesLoader()) return null;
        if (_currentInstance() is not { } instanceId) return null;

        try
        {
            return InstanceEnvironment.Resolve(_gameRoot(), instanceId).LoaderSlug;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
