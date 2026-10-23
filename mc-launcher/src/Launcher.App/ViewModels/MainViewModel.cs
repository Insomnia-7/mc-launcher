using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.Application.Abstractions;
using Launcher.Application.Auth;
using Launcher.Application.Diagnostics;
using Launcher.Application.Export;
using Launcher.Application.Java;
using Launcher.Application.Install;
using Launcher.Application.Instances;
using Launcher.Application.Launch;
using Launcher.Application.Mods;
using Launcher.Application.Settings;
using Launcher.Application.Update;
using Launcher.Application.Versions;
using Launcher.Domain.Content;
using Launcher.Domain.Java;
using Launcher.Domain.Launch;
using Launcher.Domain.Loaders;
using Launcher.Domain.Versions;
using Launcher.Infrastructure;
using Launcher.Infrastructure.Update;
using Microsoft.Extensions.Logging;

namespace Launcher.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private const string DefaultVersion = "1.21.4";

    private readonly ILogger<MainViewModel> _logger;
    private readonly GameInstaller _installer;
    private readonly ModpackInstaller _modpackInstaller;
    private readonly IModpackSourceFactory _modpackFactory;
    private readonly IJavaLocator _javaLocator;
    private readonly IProcessLauncher _processLauncher;
    private readonly VersionLoader _versionLoader;
    private readonly IMicrosoftAuthService _auth;
    private readonly IJavaProvisioner _javaProvisioner;
    private readonly DownloadSourceProvider _downloadSource;
    private readonly IUpdateClient _updateClient;
    private readonly ISecretProtector? _secretProtector;
    private readonly ModUpdateInstaller _modUpdateInstaller;
    private readonly ModUpdateService _modUpdates;
    private readonly InstanceExportService _exportService;
    private AccountLibrary? _library;

    /// <summary>正在运行的版本（versionId → pid）。同一个版本不允许同时开两份：日志、natives 会互相踩。</summary>
    private readonly Dictionary<string, int> _running = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>当前耗时命令的取消源。只在「真正干活」期间存在，命令返回前一定清掉，别让它活过命令生命周期。</summary>
    private CancellationTokenSource? _runningCts;

    public MainViewModel(
        ILogger<MainViewModel> logger,
        ILogger<DownloadViewModel> downloadLogger,
        GameInstaller installer,
        ModpackInstaller modpackInstaller,
        IModpackSourceFactory modpackFactory,
        IJavaLocator javaLocator,
        IProcessLauncher processLauncher,
        IModrinthClient modrinth,
        ICurseForgeClient curseForge,
        ModInstallService modInstallService,
        CurseForgeInstallService curseForgeInstallService,
        ModpackDownloadService modpackDownloadService,
        IVersionMetaClient meta,
        VersionLoader versionLoader,
        IMicrosoftAuthService auth,
        IJavaProvisioner javaProvisioner,
        DownloadSourceProvider downloadSource,
        IUpdateClient updateClient,
        ISecretProtector? secretProtector,
        ModUpdateService modUpdates,
        InstanceExportService exportService,
        ModUpdateInstaller modUpdateInstaller)
    {
        _logger = logger;
        _secretProtector = secretProtector;
        _modUpdateInstaller = modUpdateInstaller;
        _modUpdates = modUpdates;
        _exportService = exportService;
        _installer = installer;
        _modpackInstaller = modpackInstaller;
        _modpackFactory = modpackFactory;
        _javaLocator = javaLocator;
        _processLauncher = processLauncher;
        _versionLoader = versionLoader;
        _auth = auth;
        _javaProvisioner = javaProvisioner;
        _downloadSource = downloadSource;
        _updateClient = updateClient;
        // 直接写 backing field：初始化不该触发"写回 settings.json"
        _downloadSourceIndex = (int)downloadSource.Current;
        _library = AccountLibrary.Load(AppPaths.DataDirectory, _secretProtector);
        Account = _library.Selected;
        OfflineName = _library.OfflineIdentity.Name;

        GameRoot = AppPaths.GameRoot;

        Download = new DownloadViewModel(
            modrinth, curseForge, modInstallService, curseForgeInstallService, modpackDownloadService,
            installer, meta, downloadLogger,
            () => GameRoot,
            () => SelectedInstance?.InstanceId);

        Download.InstancesChanged += RefreshInstances;

        StatusText = $"就绪 · 游戏目录 {GameRoot}";
        InitializeSetup(meta);
        RefreshInstances();
        IsSetupOpen = Instances.Count == 0;
        _logger.LogInformation("主视图初始化完成：{Count} 个版本，游戏目录 {GameRoot}", Instances.Count, GameRoot);
    }

    /// <summary>
    /// 首帧之后才做的事：Java 全盘扫描、更新检查。
    /// 这些都不影响窗口长什么样，但 Java 扫描要几百毫秒，摆在构造函数里会直接拖慢冷启动。
    /// 由 App 在 window.Show() 之后调用（不要挪回构造函数）。
    /// </summary>
    public void StartBackgroundWork()
    {
        if (IsSetupOpen) _ = LoadSetupVersionsAsync();
        _ = LoadJavaOptionsAsync();
        _ = DelayedUpdateCheckAsync();
    }

    public string GameRoot { get; }

    // ── 自更新 ───────────────────────────────────────────────────────────
    // 检查放在首帧之后（DelayedUpdateCheckAsync 先等 2 秒），避免和 Java 扫描抢启动时间。

    [ObservableProperty]
    private string _updateText = string.Empty;

    [ObservableProperty]
    private bool _hasUpdate;

    [ObservableProperty]
    private bool _isUpdating;

    private UpdateManifest? _pendingUpdate;
    private string? _pendingUpdateLocation;

    /// <summary>手动「检查更新」用；正常情况下开机 2 秒后自动跑一次。</summary>
    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        try
        {
            var location = UpdateSettings.ReadUrl(AppPaths.DataDirectory);
            var result = await _updateClient.CheckAsync(BuildInfo.Version, location).ConfigureAwait(true);

            if (result.Manifest is { } manifest)
            {
                _pendingUpdate = manifest;
                _pendingUpdateLocation = result.Location;
                UpdateText = $"发现新版本 {manifest.Version}（当前 {BuildInfo.Version}）";
                HasUpdate = true;
                _logger.LogInformation("发现新版本 {Version}", manifest.Version);
                return;
            }

            _pendingUpdate = null;
            HasUpdate = false;
            UpdateText = result.Reason is { Length: > 0 } reason && reason.StartsWith("已是最新", StringComparison.Ordinal)
                ? reason
                : string.Empty;

            if (UpdateText.Length > 0)
                StatusText = UpdateText;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "检查更新失败");
        }
    }

    /// <summary>下载新版本 → 交给 mclupd 替换文件 → 主程序退出，让 updater 重启它。</summary>
    [RelayCommand]
    private async Task ApplyUpdateAsync()
    {
        using var operation = Launcher.Application.Operations.GameOperationCoordinator.Shared.TryEnter(GameRoot, "更新启动器");
        if (operation is null) { StatusText = Launcher.Application.Operations.GameOperationCoordinator.Shared.BusyMessage(GameRoot); return; }
        if (_pendingUpdate is not { } manifest || IsUpdating)
            return;

        var updater = FindUpdater();
        if (updater is null)
        {
            StatusText = "找不到更新器 mclupd.exe（应该在安装目录的 updater\\ 下）";
            return;
        }

        IsUpdating = true;
        try
        {
            var target = AppContext.BaseDirectory;
            var progress = new Progress<UpdateProgress>(report =>
            {
                ProgressPercent = report.Percent;
                StatusText = $"{report.Stage} {report.Percent:F0}% {report.Detail}";
            });

            var staging = await _updateClient.StageAsync(
                manifest,
                _pendingUpdateLocation,
                target,
                AppPaths.DataDirectory,
                progress).ConfigureAwait(true);

            StatusText = $"更新已就绪（{staging.DownloadedFiles} 个文件），正在重启…";
            _logger.LogInformation("更新暂存完成：{Directory}", staging.Directory);

            var startInfo = new ProcessStartInfo(updater)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(updater)!,
            };
            startInfo.ArgumentList.Add("--staging");
            startInfo.ArgumentList.Add(staging.Directory);
            startInfo.ArgumentList.Add("--target");
            startInfo.ArgumentList.Add(target);
            startInfo.ArgumentList.Add("--restart");
            startInfo.ArgumentList.Add("MCLauncher.exe");
            startInfo.ArgumentList.Add("--wait-pid");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());

            Process.Start(startInfo);
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "应用更新失败");
            StatusText = "更新失败：" + ErrorText.Describe(ex);
            IsUpdating = false;
            ProgressPercent = 0;
        }
    }

    private async Task DelayedUpdateCheckAsync()
    {
        // 首帧之后再上网：1.5 秒足够让窗口显示完，也不至于让用户等太久
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        await CheckUpdateAsync();
    }

    /// <summary>更新器位置：安装目录下的 updater\mclupd.exe，其次和主程序同级。</summary>
    private static string? FindUpdater()
    {
        var baseDirectory = AppContext.BaseDirectory;
        foreach (var candidate in new[]
                 {
                     Path.Combine(baseDirectory, "updater", "mclupd.exe"),
                     Path.Combine(baseDirectory, "mclupd.exe"),
                 })
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>右侧主区域的下载中心（内嵌，不弹窗）。</summary>
    public DownloadViewModel Download { get; }

    [ObservableProperty]
    private bool _isInstanceSection = true;

    public bool IsDownloadSection => !IsInstanceSection;

    [RelayCommand]
    private void ShowInstances()
    {
        IsInstanceSection = true;
    }

    [RelayCommand]
    private void ShowDownloads()
    {
        IsInstanceSection = false;
        Download.RefreshTarget();
        _ = Download.EnsureLoadedAsync();
    }

    partial void OnIsInstanceSectionChanged(bool value)
    {
        OnPropertyChanged(nameof(IsDownloadSection));
    }

    public ObservableCollection<InstanceCard> Instances { get; } = [];

    [ObservableProperty]
    private InstanceCard? _selectedInstance;

    // ---------- 版本设置 ----------

    public ObservableCollection<JavaOption> JavaOptions { get; } = [];

    [ObservableProperty]
    private JavaOption? _selectedJava;

    [ObservableProperty]
    private int _memoryMb = 4096;

    /// <summary>JVM 档位下拉的选中下标：0 默认 / 1 快速 / 2 兼容（对应 <see cref="JvmPreset"/>）。</summary>
    [ObservableProperty]
    private int _jvmPresetIndex;

    /// <summary>下载源下拉：0 官方直连 / 1 BMCLAPI 镜像。</summary>
    public IReadOnlyList<string> DownloadSources { get; } =
        ["官方（直连 Mojang / Fabric）", "镜像（BMCLAPI，国内加速）"];

    [ObservableProperty]
    private int _downloadSourceIndex;

    /// <summary>
    /// 切源立刻写 settings.json 并让 provider 丢掉缓存，所以不用等重启，
    /// 下一批下载就走新源。初始化时改的是 backing field，不会触发这里。
    /// </summary>
    partial void OnDownloadSourceIndexChanged(int value)
    {
        var source = value == 1 ? DownloadSource.Bmclapi : DownloadSource.Official;
        try
        {
            DownloadSourceStore.Write(source, AppPaths.DataDirectory);
            _downloadSource.Invalidate();
            SettingsHint = source == DownloadSource.Bmclapi
                ? "下载源：镜像（下一批下载生效）"
                : "下载源：官方（下一批下载生效）";
            StatusText = source == DownloadSource.Bmclapi ? "已切换到 BMCLAPI 镜像" : "已切换到官方源";
        }
        catch (Exception ex)
        {
            SettingsHint = "切换下载源失败：" + ErrorText.Describe(ex);
        }
    }

    [ObservableProperty]
    private string _extraJvmArgs = string.Empty;

    [ObservableProperty]
    private string _extraGameArgs = string.Empty;

    [ObservableProperty]
    private int _windowWidth = 1280;

    [ObservableProperty]
    private int _windowHeight = 720;

    [ObservableProperty]
    private bool _isSettingsOpen;

    [ObservableProperty]
    private string _settingsHint = string.Empty;

    /// <summary>启动失败时的诊断结论（含建议与日志路径），空串表示没有失败。</summary>
    [ObservableProperty]
    private string _diagnosisText = string.Empty;

    public bool HasDiagnosis => DiagnosisText.Length > 0;

    partial void OnDiagnosisTextChanged(string value) => OnPropertyChanged(nameof(HasDiagnosis));

    /// <summary>把诊断结论连同版本、游戏目录、时间拼成一份可粘贴的报告。剪贴板经常被别的进程占用，失败要能说清原因。</summary>
    [RelayCommand]
    private void CopyDiagnosis()
    {
        var report =
            $"{DiagnosisText}\n\n" +
            $"启动器：{BuildInfo.Name} {BuildInfo.Version}\n" +
            $"游戏目录：{GameRoot}\n" +
            $"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}";

        try
        {
            System.Windows.Clipboard.SetText(report);
            StatusText = "诊断信息已复制到剪贴板";
        }
        catch (Exception ex)
        {
            StatusText = "复制失败：" + ErrorText.Describe(ex);
        }
    }

    // ── 通用确认气泡：删除版本、清理磁盘都走这里，不再弹系统 MessageBox ──

    [ObservableProperty] private bool _isConfirmOpen;
    [ObservableProperty] private string _confirmTitle = string.Empty;
    [ObservableProperty] private string _confirmMessage = string.Empty;
    [ObservableProperty] private string _confirmDetail = string.Empty;
    [ObservableProperty] private string _confirmOkText = "确定";

    private Action? _confirmAction;

    public bool HasConfirmDetail => ConfirmDetail.Length > 0;

    partial void OnConfirmDetailChanged(string value) => OnPropertyChanged(nameof(HasConfirmDetail));

    private void AskConfirm(string title, string message, string detail, string okText, Action action)
    {
        ConfirmTitle = title;
        ConfirmMessage = message;
        ConfirmDetail = detail;
        ConfirmOkText = okText;
        _confirmAction = action;
        IsConfirmOpen = true;
        _logger.LogInformation("确认气泡：{Title}，IsConfirmOpen={Open}，Detail 长度 {Length}",
            title, IsConfirmOpen, detail.Length);
    }

    [RelayCommand]
    private void ConfirmOk()
    {
        IsConfirmOpen = false;
        var action = _confirmAction;
        _confirmAction = null;
        action?.Invoke();
    }

    [RelayCommand]
    private void ConfirmCancel()
    {
        IsConfirmOpen = false;
        _confirmAction = null;
    }

    partial void OnSelectedInstanceChanged(InstanceCard? value)
    {
        LoadSettings(value?.InstanceId);
        OpenGameDirectoryCommand.NotifyCanExecuteChanged();
        OpenInstanceFolderCommand.NotifyCanExecuteChanged();
        DeleteInstanceCommand.NotifyCanExecuteChanged();
        ExportInstanceCommand.NotifyCanExecuteChanged();
        CheckModUpdatesCommand.NotifyCanExecuteChanged();
        _ = RefreshModsAsync();
    }

    private void LoadSettings(string? instanceId)
    {
        if (instanceId is null)
        {
            SettingsHint = string.Empty;
            return;
        }

        var settings = InstanceSettings.Load(GameRoot, instanceId);
        MemoryMb = settings.MemoryMb;
        JvmPresetIndex = (int)settings.JvmPreset;
        ExtraJvmArgs = settings.ExtraJvmArgs ?? string.Empty;
        ExtraGameArgs = settings.ExtraGameArgs ?? string.Empty;
        WindowWidth = settings.WindowWidth;
        WindowHeight = settings.WindowHeight;

        SelectedJava =
            JavaOptions.FirstOrDefault(o => o.ExecutablePath is not null
                && string.Equals(o.ExecutablePath, settings.JavaPath, StringComparison.OrdinalIgnoreCase))
            ?? JavaOptions.FirstOrDefault();

        SettingsHint = settings.JavaPath is null ? "Java：自动（按版本要求挑最高的）" : "Java：已指定";
    }

    // ── 本地模组管理 ─────────────────────────────────────────────────────
    // 只读扫描 + 上游更新检查。真正的「更新」是删掉旧文件再调 ModInstallService，
    // 所以必须先让用户确认（旧文件可能在整合包里带中文前缀）。

    /// <summary>界面用的模组行；ToString 给 UIA / 无障碍读。</summary>
    public sealed record ModRow(
        string FileName, string Title, string Meta, bool Enabled, string? ModId, string FullPath)
    {
        public string StateText => Enabled ? "启用" : "已停用";
        public override string ToString() => $"{Title} {Meta}（{StateText}）";
    }

    /// <summary>界面用的更新候选行。</summary>
    public sealed record ModUpdateRow(
        string ModId, string FileName, string Title, string VersionText, string Source, ModUpdateCandidate Candidate)
    {
        public override string ToString() => $"{Title} {VersionText}（{Source}）";
    }

    public ObservableCollection<ModRow> InstalledMods { get; } = [];
    public ObservableCollection<ModUpdateRow> ModUpdates { get; } = [];

    [ObservableProperty]
    private string _modSummary = "先选一个版本";

    [ObservableProperty]
    private string _modWarnings = string.Empty;

    [ObservableProperty]
    private bool _hasModWarnings;

    [ObservableProperty]
    private bool _isModBusy;

    [RelayCommand]
    private async Task RefreshModsAsync()
    {
        ModUpdates.Clear();
        InstalledMods.Clear();
        ModWarnings = string.Empty;
        HasModWarnings = false;

        if (SelectedInstance is not { } instance)
        {
            ModSummary = "先选一个版本";
            return;
        }

        try
        {
            var modsDirectory = Path.Combine(GameRoot, "instances", instance.InstanceId, ".minecraft", "mods");
            var scan = await Task.Run(() => InstalledModScanner.Scan(modsDirectory)).ConfigureAwait(true);
            foreach (var mod in scan.Mods)
            {
                InstalledMods.Add(new ModRow(
                    mod.FileName,
                    mod.Name ?? Path.GetFileNameWithoutExtension(mod.FileName),
                    BuildModMeta(mod),
                    mod.Enabled,
                    mod.ModId,
                    mod.FullPath));
            }

            ModSummary = scan.Mods.Count == 0
                ? "这个版本还没有模组（把 .jar 拖进窗口也能装）"
                : $"{scan.Mods.Count} 个模组 · 启用 {scan.EnabledCount} / 停用 {scan.DisabledCount} · 共 {scan.TotalMegabytes:N1} MB";

            if (scan.DuplicateModIdCount > 0)
            {
                ModWarnings = $"有 {scan.DuplicateModIdCount} 个 modId 装了两份：{string.Join("、", scan.DuplicateModIds.Take(4))}"
                    + " —— 进游戏前建议只留一个，否则 Forge 会报 duplicate mod。";
                HasModWarnings = true;
            }
        }
        catch (Exception ex)
        {
            ModSummary = "读取模组失败：" + ErrorText.Describe(ex);
        }
    }

    private static string BuildModMeta(InstalledMod mod)
    {
        var version = string.IsNullOrWhiteSpace(mod.Version) ? null : mod.Version;
        var loader = string.IsNullOrWhiteSpace(mod.LoaderKind) ? null : mod.LoaderKind;
        var size = mod.Bytes >= 1024 * 1024
            ? $"{mod.Bytes / 1024d / 1024d:N1} MB"
            : $"{mod.Bytes / 1024d:N0} KB";
        var parts = new[] { version, loader, size }.Where(p => p is not null);
        return string.Join(" · ", parts);
    }

    [RelayCommand]
    private async Task CheckModUpdatesAsync()
    {
        if (SelectedInstance is not { } instance || IsModBusy) return;

        IsModBusy = true;
        StatusText = "正在检查模组更新…";
        try
        {
            var progress = new Progress<ModInstallProgress>(p =>
                StatusText = $"{p.Stage} {p.Percent:N0}%{(p.Detail is null ? string.Empty : " · " + p.Detail)}");
            var report = await _modUpdates.CheckAsync(GameRoot, instance.InstanceId, progress: progress,
                ct: _runningCts?.Token ?? CancellationToken.None).ConfigureAwait(true);

            ModUpdates.Clear();
            foreach (var candidate in report.Updates)
            {
                ModUpdates.Add(new ModUpdateRow(
                    candidate.ModId,
                    candidate.FileName,
                    Path.GetFileNameWithoutExtension(candidate.FileName),
                    $"{candidate.CurrentVersion ?? "?"} → {candidate.LatestVersion ?? "?"}",
                    candidate.Source == ModUpdateCandidate.SourceCurseForge ? "CurseForge" : "Modrinth",
                    candidate));
            }

            StatusText = report.UpdateCount == 0
                ? $"已检查 {report.CheckedCount} 个模组，都是最新（{report.FailedCount} 个查不到上游）"
                : $"发现 {report.UpdateCount} 个可更新（检查 {report.CheckedCount} 个，{report.FailedCount} 个查不到）";
        }
        catch (Exception ex)
        {
            StatusText = "检查模组更新失败：" + ErrorText.Describe(ex);
        }
        finally
        {
            IsModBusy = false;
        }
    }

    [RelayCommand]
    private void UpdateMod(ModUpdateRow? row)
    {
        if (row is null || SelectedInstance is not { } instance) return;

        AskConfirm(
            "更新模组",
            $"把「{row.Title}」从 {row.Candidate.CurrentVersion ?? "当前版本"} 换到 {row.Candidate.LatestVersion ?? "最新版"}？",
            $"会先准备并校验新版与缺失前置，再替换 {row.FileName}。下载失败不会删除旧文件。\n"
            + "如果这个模组是整合包自带的，更新后可能和整合包其他模组对不上版本，出问题可以重新导入整合包复原。",
            "更新",
            () => _ = ApplyModUpdateAsync(instance, row));
    }

    private async Task ApplyModUpdateAsync(InstanceCard instance, ModUpdateRow row)
    {
        using var operation = Launcher.Application.Operations.GameOperationCoordinator.Shared.TryEnter(GameRoot, "更新模组");
        if (operation is null) { StatusText = Launcher.Application.Operations.GameOperationCoordinator.Shared.BusyMessage(GameRoot); return; }
        if (IsBusy) { StatusText = "还有别的任务在跑，等它结束"; return; }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        _runningCts = cts;
        IsBusy = true;
        try
        {
            var progress = new Progress<ModInstallProgress>(p =>
                StatusText = $"{p.Stage} {p.Percent:N0}%{(p.Detail is null ? string.Empty : " · " + p.Detail)}");
            var report = await _modUpdateInstaller.UpdateAsync(
                GameRoot, instance.InstanceId, row.Candidate, progress, cts.Token).ConfigureAwait(true);

            StatusText = $"「{row.Title}」更新完成：{Path.GetFileName(report.FileName)}（{report.FileBytes / 1024d / 1024d:N1} MB）";
            await RefreshModsAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (IsCancellation(ex))
        {
            StatusText = "已取消";
        }
        catch (Exception ex)
        {
            StatusText = "更新模组失败：" + ErrorText.Describe(ex);
        }
        finally
        {
            _runningCts = null;
            IsBusy = false;
        }
    }

    // ── 导出备份 ────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ExportInstanceAsync()
    {
        using var operation = Launcher.Application.Operations.GameOperationCoordinator.Shared.TryEnter(GameRoot, "导出备份");
        if (operation is null) { StatusText = Launcher.Application.Operations.GameOperationCoordinator.Shared.BusyMessage(GameRoot); return; }
        if (IsBusy || Download.IsBusy) { StatusText = "请等待当前任务结束后再导出"; return; }
        if (SelectedInstance is not { } instance)
        {
            StatusText = "先选一个版本";
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出为一个 zip（只含这个版本的 mods / 配置 / 存档，不含共享库与资源）",
            FileName = $"{SanitizeFileName(instance.DisplayName)}-{DateTime.Now:yyyyMMdd}.zip",
            Filter = "Zip 压缩包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
            AddExtension = true,
            DefaultExt = ".zip",
        };
        if (dialog.ShowDialog() != true) return;

        using var cts = new CancellationTokenSource();
        _runningCts = cts;
        IsBusy = true;
        try
        {
            var progress = new Progress<string>(text => StatusText = text);
            var report = await _exportService.ExportAsync(
                GameRoot, instance.InstanceId, dialog.FileName, progress: progress,
                ct: cts.Token).ConfigureAwait(true);
            StatusText = report.ToSummary();
        }
        catch (Exception ex) when (IsCancellation(ex))
        {
            StatusText = "已取消";
        }
        catch (Exception ex)
        {
            StatusText = "导出失败：" + ErrorText.Describe(ex);
        }
        finally
        {
            if (ReferenceEquals(_runningCts, cts)) _runningCts = null;
            IsBusy = false;
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "instance" : cleaned;
    }

    [RelayCommand]
    private async Task LoadJavaOptionsAsync()
    {
        try
        {
            var javas = await _javaLocator.DiscoverAsync(CancellationToken.None).ConfigureAwait(true);
            JavaOptions.Clear();
            JavaOptions.Add(JavaOption.Auto);
            foreach (var java in javas.OrderByDescending(j => j.MajorVersion))
                JavaOptions.Add(new JavaOption($"Java {java.MajorVersion}", java.ExecutablePath, java.MajorVersion));

            LoadSettings(SelectedInstance?.InstanceId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "扫描 Java 失败");
        }
    }

    [RelayCommand]
    private void SaveSettings()
    {
        using var operation = Launcher.Application.Operations.GameOperationCoordinator.Shared.TryEnter(GameRoot, "保存实例设置");
        if (operation is null) { StatusText = Launcher.Application.Operations.GameOperationCoordinator.Shared.BusyMessage(GameRoot); return; }
        if (SelectedInstance is not { } instance)
        {
            SettingsHint = "先选一个版本";
            return;
        }

        var settings = new InstanceSettings(
            MemoryMb: MemoryMb,
            JavaPath: SelectedJava?.ExecutablePath,
            JvmPreset: (JvmPreset)JvmPresetIndex,
            ExtraJvmArgs: ExtraJvmArgs,
            ExtraGameArgs: ExtraGameArgs,
            WindowWidth: WindowWidth,
            WindowHeight: WindowHeight).Normalized();

        try
        {
            settings.Save(GameRoot, instance.InstanceId);
            MemoryMb = settings.MemoryMb;
            SettingsHint = $"已保存 · {settings.MemoryMb} MB · {(settings.JavaPath is null ? "Java 自动" : "Java 指定")}";
            StatusText = $"版本 {instance.DisplayName} 的设置已保存";
        }
        catch (Exception ex)
        {
            SettingsHint = "保存失败：" + ErrorText.Describe(ex);
        }
    }

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>有任务在跑时才显示「取消」按钮。</summary>
    [ObservableProperty]
    private bool _canCancel;

    [ObservableProperty]
    private double _progressPercent;

    // ── 正版账号 ─────────────────────────────────────────────────────────
    [ObservableProperty] private MicrosoftAccount? _account;
    [ObservableProperty] private string _loginCode = string.Empty;
    [ObservableProperty] private string _loginHint = string.Empty;
    [ObservableProperty] private bool _isLoggingIn;

    public string AccountText =>
        Account is null
            ? $"离线 · {_library?.OfflineIdentity.Name ?? "Developer"}"
            : $"{Account.Name}（正版）";

    public string AccountDetail => Account is null
        ? "没登录正版账号，用离线身份进游戏，进不了需要正版验证的服务器"
        : $"正版账号 · 令牌{(Account.NeedsRefresh ? "即将过期，启动时自动续期" : "有效")}";

    public bool HasAccount => Account is not null;

    /// <summary>离线用户名，直接绑在界面输入框上；失焦时写盘并重算 UUID。</summary>
    [ObservableProperty] private string _offlineName = "Developer";

    partial void OnOfflineNameChanged(string value)
    {
        if (_library is null) return;

        try
        {
            _library.SetOfflineName(value);
            _library.Save(AppPaths.DataDirectory, _secretProtector);
            StatusText = $"离线用户名改成了 {_library.OfflineIdentity.Name}";
        }
        catch (ArgumentException)
        {
            StatusText = "离线用户名只能 3–16 位字母、数字或下划线";
        }

        OnPropertyChanged(nameof(AccountText));
    }

    partial void OnAccountChanged(MicrosoftAccount? value)
    {
        OnPropertyChanged(nameof(AccountText));
        OnPropertyChanged(nameof(AccountDetail));
        OnPropertyChanged(nameof(HasAccount));
        OnPropertyChanged(nameof(SetupIdentityText));
    }

    partial void OnIsLoggingInChanged(bool value)
    {
        InstallSetupCommand.NotifyCanExecuteChanged();
        CloseSetupCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task LoginAsync(CancellationToken cancellationToken)
    {
        if (IsLoggingIn) return;

        IsLoggingIn = true;
        LoginCode = string.Empty;
        LoginHint = string.Empty;

        try
        {
            StatusText = "正在申请登录代码…";
            var session = await _auth.StartDeviceCodeAsync(cancellationToken);

            LoginCode = session.UserCode;
            CopyToClipboard(session.UserCode);
            LoginHint = $"已在浏览器打开 {session.VerificationUri}，代码 {session.UserCode} 已复制到剪贴板，粘贴进去即可";
            OpenInBrowser(session.VerificationUri);
            StatusText = "等待你在浏览器里确认…";

            var progress = new Progress<string>(message => LoginHint = message);
            var account = await _auth.WaitForLoginAsync(session, progress, cancellationToken);

            _library?.AddOrUpdate(account);
            _library?.Save(AppPaths.DataDirectory, _secretProtector);
            Account = account;

            LoginCode = string.Empty;
            LoginHint = string.Empty;
            StatusText = $"已登录：{account.Name}";
            _logger.LogInformation("微软登录成功：{Account}", account);
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消登录";
            LoginCode = string.Empty;
            LoginHint = string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "微软登录失败");
            StatusText = "登录失败：" + ErrorText.Describe(ex);
            LoginCode = string.Empty;
            LoginHint = string.Empty;
        }
        finally
        {
            IsLoggingIn = false;
        }
    }

    /// <summary>用系统默认浏览器打开链接；失败只记日志，不打断登录流程。</summary>
    private void OpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "打开浏览器失败：{Url}", url);
        }
    }

    /// <summary>把设备码放进剪贴板，省得用户手抄。</summary>
    private void CopyToClipboard(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            // 剪贴板被别的程序占着是常事，不值得让登录停下来
            _logger.LogDebug(ex, "写剪贴板失败");
        }
    }

    [RelayCommand]
    private void Logout()
    {
        if (Account is { } current)
        {
            _library?.Remove(current.Uuid);
            _library?.Save(AppPaths.DataDirectory, _secretProtector);
        }

        Account = null;
        StatusText = "已退出正版账号，回到离线身份";
    }

    /// <summary>启动前把要过期的令牌换掉；续期失败就退回离线，不让登录状态挡住进游戏。</summary>
    private async Task<MicrosoftAccount?> EnsureFreshAccountAsync(CancellationToken ct)
    {
        if (Account is null) return null;
        if (!Account.NeedsRefresh) return Account;

        try
        {
            StatusText = "正版令牌快过期了，正在续期…";
            var fresh = await _auth.RefreshAsync(Account, ct);
            _library?.AddOrUpdate(fresh);
            _library?.Save(AppPaths.DataDirectory, _secretProtector);
            Account = fresh;
            return fresh;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "续期失败，本次用离线身份启动");
            StatusText = "续期失败，本次按离线身份启动";
            return null;
        }
    }
    public string EmptyStateHint =>
        Instances.Count == 0
            ? "还没有版本。\n点击左下角「安装向导」一键准备，或前往「下载」选择版本。"
            : "「启动游戏」使用上方显示的身份启动当前版本；\n"
              + "左侧「下载」里可以装模组、整合包、资源包、数据包与光影包；\n"
              + "下面的「版本设置」能改内存、Java 和额外参数，保存后写进这个版本的 settings.json。";

    partial void OnIsBusyChanged(bool value)
    {
        InstallSetupCommand.NotifyCanExecuteChanged();
        InstallCommand.NotifyCanExecuteChanged();
        ImportModpackCommand.NotifyCanExecuteChanged();
        LaunchCommand.NotifyCanExecuteChanged();
        CanCancel = value;
    }

    /// <summary>
    /// 取消当前耗时任务。这里只发信号：收尾（IsBusy、进度条、_runningCts）都交给命令自己的 finally，
    /// 免得按钮把状态提前放掉、命令还在跑。
    /// </summary>
    [RelayCommand]
    private void Cancel()
    {
        if (_runningCts is not { } cts || cts.IsCancellationRequested) return;

        try
        {
            cts.Cancel();
            StatusText = "正在取消…";
            _logger.LogInformation("用户请求取消当前任务");
        }
        catch (Exception ex)
        {
            // 命令恰好在这一瞬间收尾并释放了 cts，或者某个注册回调自己抛了；都不该让 UI 线程挂掉
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

    private bool CanInstall() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        using var operation = Launcher.Application.Operations.GameOperationCoordinator.Shared.TryEnter(GameRoot, "安装游戏");
        if (operation is null) { StatusText = Launcher.Application.Operations.GameOperationCoordinator.Shared.BusyMessage(GameRoot); return; }
        IsBusy = true;
        ProgressPercent = 0;
        StatusText = $"开始安装 {DefaultVersion}…";

        // 取消源在真正开始干活时才建：放命令入口会被并行命令相互覆盖
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        _runningCts = cts;

        try
        {
            var progress = new Progress<DownloadProgress>(p =>
            {
                ProgressPercent = p.Percent;
                StatusText = $"{p.Percent:F0}%  {p.CompletedBytes / 1024.0 / 1024.0:F1} MB  {p.CurrentFile}";
            });

            var report = await _installer.InstallAsync(DefaultVersion, GameRoot, includeAssets: true, progress, cts.Token);
            _logger.LogInformation("安装完成：{Report}", report);
            StatusText = "安装完成：" + report;
            RefreshInstances();
        }
        catch (Exception ex) when (IsCancellation(ex))
        {
            _logger.LogInformation(ex, "安装已取消");
            StatusText = "已取消";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "安装失败");
            StatusText = "安装失败：" + ErrorText.Describe(ex);
        }
        finally
        {
            if (ReferenceEquals(_runningCts, cts)) _runningCts = null;
            IsBusy = false;
            ProgressPercent = 0;
        }
    }

    private bool CanLaunch() => !IsBusy && SelectedInstance is not null;

    [RelayCommand(CanExecute = nameof(CanLaunch))]
    private async Task LaunchAsync()
    {
        using var operation = Launcher.Application.Operations.GameOperationCoordinator.Shared.TryEnter(GameRoot, "准备游戏启动");
        if (operation is null) { StatusText = Launcher.Application.Operations.GameOperationCoordinator.Shared.BusyMessage(GameRoot); return; }
        if (SelectedInstance is not { } instance) return;

        // 同一版本已经开着就拦住：再启一份会抢 logs/latest.log 和 natives 目录
        lock (_running)
        {
            if (_running.TryGetValue(instance.VersionId, out var runningPid))
            {
                StatusText = $"「{instance.DisplayName}」已经在运行（pid {runningPid}），先关掉再启动";
                return;
            }
        }

        DiagnosisText = string.Empty;
        IsBusy = true;

        // 启动链路上的每一步（续期、读版本、找 Java、下载 Java、体检）都吃同一个 token
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        _runningCts = cts;

        try
        {
            var versionPath = Path.Combine(GameRoot, "versions", instance.VersionId, instance.VersionId + ".json");
            if (!File.Exists(versionPath))
            {
                StatusText = $"找不到版本文件：{versionPath}";
                return;
            }

            // Forge / NeoForge 的版本 json 只有增量，必须沿 inheritsFrom 合并出完整的
            // mainClass / libraries / arguments，否则 classpath 是空的，java 只会报「找不到主类」
            // 令牌快过期就先换新的；续期失败会回 null，本次按离线身份走
            var account = await EnsureFreshAccountAsync(cts.Token);

            var version = await _versionLoader.LoadAsync(GameRoot, instance.VersionId, cts.Token);

            var settings = InstanceSettings.Load(GameRoot, instance.InstanceId).Normalized();
            var required = version.JavaVersion?.MajorVersion ?? 8;
            var javas = await _javaLocator.DiscoverAsync(cts.Token);

            // 用户在版本设置里钉死的 Java 优先；找不到就退回按版本要求自动挑
            JavaInstall? java = null;
            if (settings.JavaPath is { Length: > 0 } pinned)
            {
                java = javas.FirstOrDefault(j =>
                        string.Equals(j.ExecutablePath, pinned, StringComparison.OrdinalIgnoreCase))
                    ?? await _javaLocator.ProbeAsync(pinned, cts.Token);
            }

            java ??= javas.Where(j => j.MajorVersion >= required).OrderBy(j => j.MajorVersion).FirstOrDefault();

            if (java is null)
            {
                // 本机一个合用的都没有，直接按大版本装一份到数据目录（Adoptium JRE）
                StatusText = $"本机没有 Java {required}+（找到 {javas.Count} 个），正在自动下载…";
                var javaProgress = new Progress<JavaProvisionProgress>(p =>
                {
                    ProgressPercent = p.Percent;
                    StatusText = $"安装 Java {required}：{p.Stage}" + (p.Detail is null ? string.Empty : $" · {p.Detail}");
                });

                java = await _javaProvisioner.EnsureAsync(required, AppPaths.DataDirectory, javaProgress, cts.Token);
                StatusText = $"Java 就绪：{java.ExecutablePath}";
                ProgressPercent = 0;
            }

            var jarVersionId = version.LauncherParentId ?? version.Id;
            var context = new LaunchContext(
                PlayerName: account?.Name ?? (_library?.OfflineIdentity.Name ?? "Developer"),
                Uuid: account?.Uuid ?? (_library?.OfflineIdentity.Uuid ?? LaunchContext.OfflineUuid("Developer")),
                AccessToken: account?.AccessToken,
                UserType: account is null ? "legacy" : "msa",
                LauncherName: BuildInfo.Name,
                LauncherVersion: BuildInfo.Version,
                GameDirectory: Path.Combine(GameRoot, "instances", instance.InstanceId, ".minecraft"),
                AssetsRoot: Path.Combine(GameRoot, "assets"),
                LibrariesRoot: Path.Combine(GameRoot, "libraries"),
                ClientJarPath: Path.Combine(GameRoot, "versions", jarVersionId, jarVersionId + ".jar"),
                MemoryMb: settings.MemoryMb,
                JvmPreset: settings.JvmPreset,
                ExtraJvmArgs: settings.ExtraJvmArgs,
                ExtraGameArgs: settings.ExtraGameArgs,
                WindowWidth: settings.WindowWidth,
                WindowHeight: settings.WindowHeight,
                FullScreen: settings.FullScreen);

            // 先把 classpath 算好、natives 解到稳定目录（同指纹直接复用），
            // 再拿这份「准备好的上下文」去体检与拼参数，三处用的目录必须一致。
            var prep = await LaunchPreparer.PrepareAsync(version, PlatformContext.Current, context, GameRoot, cts.Token);
            var prepared = prep.Context;

            // 启动前体检：缺库 / 缺 natives / 缺资源索引时，先给一句人话，
            // 比让 java 抛一屏 ClassNotFoundException 强。
            var preflight = await LaunchPreflight.CheckAsync(version, PlatformContext.Current, prepared, cts.Token, prep.ClassPath);
            if (!preflight.IsReady)
            {
                StatusText = preflight.Summary();
                DiagnosisText = "缺少启动必需的文件\n\n" + preflight.Describe();
                return;
            }

            var plan = LaunchPlanBuilder.Build(version, PlatformContext.Current, prepared, java.ExecutablePath, prep.ClassPath);

            // 游戏目录不可写时，Minecraft 会在 log4j 初始化阶段就崩（写不了 logs/latest.log），
            // 提前探测比让用户看崩溃报告强。
            if (!await IsDirectoryWritableAsync(context.GameDirectory))
            {
                StatusText = $"游戏目录不可写：{context.GameDirectory}";
                return;
            }

            var logPath = Path.Combine(GameRoot, "logs", $"{version.Id}-{DateTime.Now:yyyyMMdd-HHmmss}.log");

            var tempRoot = Path.Combine(GameRoot, ".tmp");
            Directory.CreateDirectory(tempRoot);

            var process = _processLauncher.Start(plan, logPath, new Dictionary<string, string>
            {
                ["TEMP"] = tempRoot,
                ["TMP"] = tempRoot,
                ["TMPDIR"] = tempRoot,
            });

            StatusText = $"已启动（pid {process.Id}）· Java {java.MajorVersion} · 日志 {logPath}";
            _logger.LogInformation("启动 {Version}，pid={Pid}，java={Java}", version.Id, process.Id, java.ExecutablePath);

            lock (_running) _running[version.Id] = process.Id;

            _ = WatchStartupAsync(process, logPath, version.Id);
        }
        catch (Exception ex) when (IsCancellation(ex))
        {
            // 取消不等于失败：清掉诊断气泡，别让用户以为启动器判断出毛病了
            _logger.LogInformation(ex, "启动已取消");
            StatusText = "已取消";
            DiagnosisText = string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动失败");
            StatusText = "启动失败：" + ErrorText.Describe(ex);
        }
        finally
        {
            if (ReferenceEquals(_runningCts, cts)) _runningCts = null;
            IsBusy = false;
        }
    }

    /// <summary>探一下目录能不能写：Minecraft 写不了 logs/latest.log 会直接崩在 log4j 初始化。</summary>
    private static async Task<bool> IsDirectoryWritableAsync(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".write-test-{Guid.NewGuid():N}");
            await File.WriteAllTextAsync(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// 盯启动结果。参数错 / Java 不对会在两三秒内退出，模组冲突往往十几秒后才炸，
    /// 所以不能只看一眼：每 2 秒轮询一次，跑满 2 分钟就当它起来了。
    /// </summary>
    private async Task WatchStartupAsync(IGameProcess process, string logPath, string versionId)
    {
        try
        {
            await WatchStartupCoreAsync(process, logPath);
        }
        finally
        {
            // 进程退出（正常 / 崩溃 / 我们主动杀）都要把占位放掉，否则这个版本再也起不来
            lock (_running)
            {
                if (_running.TryGetValue(versionId, out var pid) && pid == process.Id)
                    _running.Remove(versionId);
            }
        }
    }

    private async Task WatchStartupCoreAsync(IGameProcess process, string logPath)
    {
        try
        {
            var startedAt = DateTime.UtcNow;
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                if (process.HasExited) break;
                if ((DateTime.UtcNow - startedAt).TotalSeconds > 120) return;
            }
        }
        catch (Exception)
        {
            return;
        }

        // 崩溃结论都在日志尾部，认出来比让用户自己翻两千行 stack trace 有用
        var diagnosis = StartupDiagnostics.Analyze(logPath);

        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (diagnosis is null)
            {
                StatusText = $"启动后立即退出 · 看日志：{logPath}";
                return;
            }

            _logger.LogWarning("启动失败：{Summary} | {Advice}", diagnosis.Summary, diagnosis.Advice);
            StatusText = "启动失败：" + diagnosis.Summary;
            DiagnosisText = $"{diagnosis.Summary}\n建议：{diagnosis.Advice}\n日志：{logPath}";
        });
    }

    private bool CanOpenFolder() => SelectedInstance is not null;

    [RelayCommand(CanExecute = nameof(CanOpenFolder))]
    private void OpenGameDirectory()
    {
        if (SelectedInstance is not { } instance) return;
        if (ResolveInstancePath(instance, "instances") is not { } path) return;
        OpenInExplorer(Path.Combine(path, ".minecraft"));
    }

    [RelayCommand]
    private void OpenLogs() => OpenInExplorer(Path.Combine(GameRoot, "logs"));

    [RelayCommand(CanExecute = nameof(CanOpenFolder))]
    private void OpenInstanceFolder()
    {
        if (SelectedInstance is not { } instance) return;
        if (ResolveInstancePath(instance, "instances") is not { } path) return;
        OpenInExplorer(path);
    }

    [RelayCommand(CanExecute = nameof(CanOpenFolder))]
    private void DeleteInstance()
    {
        if (SelectedInstance is not { } instance) return;
        if (ResolveInstancePath(instance, "instances") is not { } instancePath) return;
        if (ResolveInstancePath(instance, "versions") is not { } versionPath) return;

        AskConfirm(
            $"删除「{instance.DisplayName}」？",
            "mods、config、存档都会一起删掉，删完找不回来。共享的 libraries 和 assets 会保留。",
            instancePath + Environment.NewLine + versionPath,
            "删除",
            () => DeleteInstanceCore(instance, instancePath, versionPath));
    }

    private void DeleteInstanceCore(InstanceCard instance, string instancePath, string versionPath)
    {
        using var operation = Launcher.Application.Operations.GameOperationCoordinator.Shared.TryEnter(GameRoot, "删除实例");
        if (operation is null) { StatusText = Launcher.Application.Operations.GameOperationCoordinator.Shared.BusyMessage(GameRoot); return; }
        try
        {
            var result = InstanceDeletion.Delete(GameRoot, instance.InstanceId, instance.VersionId);
            var stillUsed = result.VersionRetained;

            StatusText = stillUsed
                ? $"已删除版本 {instance.DisplayName}（版本文件仍被其他实例或加载器依赖，已保留）"
                : $"已删除版本 {instance.DisplayName}";
            _logger.LogInformation("删除版本 {InstanceId}（version {VersionId}，共用={Shared}）",
                instance.InstanceId, instance.VersionId, stillUsed);
            RefreshInstances();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除版本失败");
            StatusText = "删除失败：" + ErrorText.Describe(ex);
        }
    }


    /// <summary>
    /// 把 id 拼到 gameRoot 下的某个子目录。instances 用实例 id，versions 用版本 id
    /// （同一个版本目录可能被多个实例共用，两者不一定相同）。id 来自目录名，正常不含分隔符；
    /// 真含了就当非法输入拒掉，免得删错地方。
    /// </summary>
    private string? ResolveInstancePath(InstanceCard instance, string category)
    {
        var id = string.Equals(category, "versions", StringComparison.OrdinalIgnoreCase)
            ? instance.VersionId
            : instance.InstanceId;

        if (id.IndexOfAny(['\\', '/', ':']) >= 0 || id is "." or "..")
        {
            StatusText = $"版本 id 非法：{id}";
            return null;
        }

        return Path.Combine(GameRoot, category, id);
    }

    private static void OpenInExplorer(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception)
        {
            // 打不开资源管理器不影响启动器本身，用户能自己去找
        }
    }

    private bool CanImportModpack() => !IsBusy;

    /// <summary>导入整合包（.mrpack / .zip）：原版 → 加载器 → 清单文件 → overrides。Modrinth 与 CurseForge 两种格式都认。</summary>
    [RelayCommand(CanExecute = nameof(CanImportModpack))]
    private async Task ImportModpackAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择整合包",
            Filter = "整合包 (*.mrpack;*.zip)|*.mrpack;*.zip|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true) return;

        await ImportModpackFileAsync(dialog.FileName);
    }

    /// <summary>导入 MultiMC / Prism 实例文件夹：它不是一个压缩包，而是一整个实例目录（含 mmc-pack.json）。</summary>
    [RelayCommand(CanExecute = nameof(CanImportModpack))]
    private async Task ImportFolderAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择 MultiMC / Prism 实例文件夹（里面要有 mmc-pack.json）",
        };

        if (dialog.ShowDialog() != true) return;

        await ImportModpackFileAsync(dialog.FolderName);
    }

    /// <summary>按路径导入整合包：菜单、对话框、拖拽三个入口最后都落到这里。</summary>
    public async Task ImportModpackFileAsync(string path)
    {
        using var operation = Launcher.Application.Operations.GameOperationCoordinator.Shared.TryEnter(GameRoot, "导入整合包");
        if (operation is null) { StatusText = Launcher.Application.Operations.GameOperationCoordinator.Shared.BusyMessage(GameRoot); return; }
        if (IsBusy)
        {
            StatusText = "有任务在跑，等它结束再导入";
            return;
        }

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            StatusText = $"找不到文件：{path}";
            return;
        }

        // 目录 = MultiMC / Prism 实例（里面要有 mmc-pack.json），只有文件才看扩展名
        if (File.Exists(path))
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension is not (".zip" or ".mrpack"))
            {
                StatusText = $"不支持的格式：{extension}（整合包应该是 .zip 或 .mrpack）";
                return;
            }
        }

        IsBusy = true;
        ProgressPercent = 0;
        StatusText = $"正在打开整合包：{Path.GetFileName(path)}";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        _runningCts = cts;

        try
        {
            await using var source = _modpackFactory.Open(path);
            var pack = source.Pack;

            _logger.LogInformation(
                "整合包 {Name} {Version}：MC {Mc} · 来源 {Source}",
                pack.Name, pack.VersionId, pack.MinecraftVersion ?? "-", pack.SourceLabel);

            var progress = new Progress<ModpackImportProgress>(p =>
            {
                ProgressPercent = p.Percent;
                StatusText = string.IsNullOrEmpty(p.Detail) ? p.Stage : $"{p.Stage} · {p.Detail}";
            });

            var report = await _modpackInstaller.ImportAsync(source, GameRoot, progress, cts.Token);

            _logger.LogInformation("整合包导入完成：{Report}", report);
            StatusText = $"「{report.PackName}」导入完成 · {report.InstanceId} · 下载 {report.DownloadedFiles} / 跳过 {report.SkippedFiles} · overrides {report.OverrideFiles} 个";

            // 部分成功：文件少了几个，游戏可能起不来，把清单直接摊在状态栏上
            if (report.MissingCount > 0)
                StatusText += $" · 还差 {report.MissingCount} 个文件：{report.MissingSummary}";

            RefreshInstances();
            SelectedInstance = Instances.FirstOrDefault(i => i.InstanceId == report.InstanceId) ?? Instances.FirstOrDefault();
        }
        catch (Exception ex) when (IsCancellation(ex))
        {
            _logger.LogInformation(ex, "整合包导入已取消");
            StatusText = "已取消";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "整合包导入失败");
            StatusText = "整合包导入失败：" + ErrorText.Describe(ex);
        }
        finally
        {
            if (ReferenceEquals(_runningCts, cts)) _runningCts = null;
            IsBusy = false;
            ProgressPercent = 0;
        }
    }

    /// <summary>拖 .jar 进窗口 = 装进当前版本的 mods。资源包 / 光影包请走下载页。</summary>
    public async Task DropJarAsync(string jarPath)
    {
        using var operation = Launcher.Application.Operations.GameOperationCoordinator.Shared.TryEnter(GameRoot, "导入模组");
        if (operation is null) { StatusText = Launcher.Application.Operations.GameOperationCoordinator.Shared.BusyMessage(GameRoot); return; }
        if (SelectedInstance is not { } instance)
        {
            StatusText = "先选一个版本，再把 .jar 拖进来";
            return;
        }

        if (IsBusy)
        {
            StatusText = "有任务在跑，等它结束再拖";
            return;
        }

        try
        {
            var modsDirectory = Path.Combine(GameRoot, "instances", instance.InstanceId, ".minecraft", "mods");
            Directory.CreateDirectory(modsDirectory);

            var fileName = Path.GetFileName(jarPath);
            var destination = Path.Combine(modsDirectory, fileName);
            if (File.Exists(destination))
            {
                StatusText = $"「{fileName}」已经在这个版本里了，跳过";
                return;
            }

            await Task.Run(() => File.Copy(jarPath, destination, overwrite: false));
            _logger.LogInformation("拖入模组 {File} → {Instance}", fileName, instance.InstanceId);
            StatusText = $"已把 {fileName} 放进「{instance.DisplayName}」的 mods";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "拖入模组失败：{File}", jarPath);
            StatusText = "拖入失败：" + ErrorText.Describe(ex);
        }
    }

    private void RefreshInstances()
    {
        // 重建列表会换掉版本对象，尽量把用户原来的选择留住
        var previousId = SelectedInstance?.InstanceId;
        Instances.Clear();

        // 实例列表以 instances/index.json 为准（旧数据 LoadOrMigrate 会顺手补建）。
        // 注意：实例目录名不一定等于版本目录名——同一个加载器版本可以被多个整合包共用
        // （fabric-loader-0.20.1-1.21.4 与 fabric-loader-0.20.1-1.21.4-2 共用一份 versions）。
        var registry = InstanceRegistry.LoadOrMigrate(GameRoot);
        foreach (var registration in registry.Instances)
        {
            var id = registration.Id;
            var versionId = registration.VersionId;

            // 版本文件没了（用户手删过 versions）就先不列，免得点启动直接报错
            if (!File.Exists(Path.Combine(GameRoot, "versions", versionId, versionId + ".json")))
            {
                _logger.LogWarning("实例 {Instance} 指向的版本 {Version} 不存在，已跳过", id, versionId);
                continue;
            }

            var jarVersion = versionId;
            try
            {
                using var stream = File.OpenRead(Path.Combine(GameRoot, "versions", versionId, versionId + ".json"));
                using var document = JsonDocument.Parse(stream);
                if (document.RootElement.TryGetProperty("x-launcher-parent", out var parent))
                    jarVersion = parent.GetString() ?? versionId;
                else if (document.RootElement.TryGetProperty("inheritsFrom", out var inherits))
                    jarVersion = inherits.GetString() ?? versionId;
            }
            catch (JsonException)
            {
                // 损坏的版本文件先列出来，别让它把界面搞挂
            }

            var metadata = InstanceMetadata.TryRead(GameRoot, id);
            var parsed = VersionIdParser.Parse(versionId, jarVersion == versionId ? null : jarVersion);

            string title;
            string subtitle;
            if (metadata is not null)
            {
                // 整合包：第一行包名 + 包版本，第二行加载器 + 游戏本体版本
                title = metadata.ToDisplayTitle(id);
                subtitle = metadata.ToDisplaySubtitle() ?? parsed.GameVersion;
            }
            else if (parsed.IsLoader)
            {
                // 加载器版本：第一行加载器 + 版本，第二行游戏本体版本
                title = parsed.Title(versionId);
                subtitle = parsed.GameVersion;
            }
            else
            {
                title = versionId;
                subtitle = "原版";
            }

            Instances.Add(new InstanceCard(id, versionId, title, subtitle, jarVersion, metadata is not null));
        }

        // 选择优先级：上次选的 → 整合包（有 instance.json 的）→ 第一个
        SelectedInstance =
            Instances.FirstOrDefault(i => i.InstanceId == previousId) ??
            Instances.FirstOrDefault(i => i.IsModpack) ??
            Instances.FirstOrDefault();
    }
}

public sealed class InstanceCard(
    string instanceId,
    string versionId,
    string displayName,
    string subtitle,
    string baseVersion,
    bool isModpack)
{
    /// <summary>实例目录名（instances/&lt;InstanceId&gt;）。整合包可能带 -2 后缀。</summary>
    public string InstanceId { get; } = instanceId;

    /// <summary>版本目录名（versions/&lt;VersionId&gt;）。多个实例可以共用同一个。</summary>
    public string VersionId { get; } = versionId;

    public string BaseVersion { get; } = baseVersion;
    public string DisplayName { get; } = displayName;
    public string Subtitle { get; } = subtitle;
    public bool IsLoader { get; } = baseVersion != versionId;
    public bool IsModpack { get; } = isModpack;

    /// <summary>给 UI 自动化 / 屏幕阅读器用的可读名字（默认会念成类型名）。</summary>
    public override string ToString() => DisplayName;
}

/// <summary>版本设置里的 Java 下拉项；<see cref="Auto"/> 表示按版本要求自动挑。</summary>
public sealed record JavaOption(string Title, string? ExecutablePath, int MajorVersion)
{
    public static readonly JavaOption Auto = new("自动选择", null, 0);

    public override string ToString() => Title;
}
