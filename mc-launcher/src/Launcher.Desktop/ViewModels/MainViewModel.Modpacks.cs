using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.Application.Experience;
using Launcher.Application.Install;
using Launcher.Application.Mods;
using Launcher.Application.Operations;

namespace Launcher.App.ViewModels;

public sealed partial class PackChangeRow(PackFileChange change) : ObservableObject
{
    public PackFileChange Change { get; } = change;
    public string Path => Change.Path;
    public string Kind => Change.Kind;
    public string Reason => Change.Reason;
    public bool IsConflict => Change.IsConflict;
    [ObservableProperty] private bool _apply = change.ApplyByDefault;
    public string ChoiceLabel => Change.After is null ? "移除这个文件" : Change.Kind == "合并" ? "应用合并结果" : "采用新版内容";
}

public sealed partial class MainViewModel
{
    private ModpackUpdateService _packUpdates = null!;
    private PackUpdatePlan? _packPlan;
    private CancellationTokenSource? _packPreparation;
    private string? _packRoot, _packInstance;
    [ObservableProperty] private bool _packBusy;
    [ObservableProperty] private string _packHint = "检查平台版本，或选择本地整合包，先预览差异再更新。";
    [ObservableProperty] private ModpackRelease? _selectedPackRelease;
    [ObservableProperty] private string _packChannel = "正式版";
    public string[] PackChannels { get; } = ["正式版", "测试版", "开发版", "全部版本"];
    public ObservableCollection<ModpackRelease> PackReleases { get; } = [];
    private IReadOnlyList<ModpackRelease> _allPackReleases = [];
    public ObservableCollection<PackChangeRow> PackChanges { get; } = [];
    public bool IsPackDetailTab => DetailTab == 5;
    public bool HasPackPlan => _packPlan is not null;
    public bool CanManagePack => CanEditExperience && !PackBusy;
    public string PackPlanSummary => _packPlan?.Summary ?? "尚未准备更新预览";
    public string PackIdentity => SelectedInstance is {} instance ? InstanceMetadata.TryRead(GameRoot, instance.InstanceId)?.ToDisplayTitle(instance.InstanceId) ?? SelectedGameName : "整合包";
    public bool HasPackRecovery { get; private set; }
    private bool PackPlanHasTask(PackUpdatePlan plan) => _tasks.List().Any(t => t.Request.Arguments.GetValueOrDefault("plan") == Path.Combine(plan.Stage,"plan.json") && t.State is DurableTaskState.Ready or DurableTaskState.Running or DurableTaskState.Paused or DurableTaskState.Failed);

    private void InitializePackWorkbench()
    {
        _packUpdates = _modpackInstaller.CreateUpdateService();
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SelectedInstance) or nameof(GameRoot)) ResetPackContext();
            if (e.PropertyName is nameof(CanEditExperience) or nameof(PackBusy)) OnPropertyChanged(nameof(CanManagePack));
            if (e.PropertyName == nameof(DetailTab)) { OnPropertyChanged(nameof(IsPackDetailTab)); if (IsPackDetailTab) RefreshPackRecovery(); }
        };
        BeforeLaunchChecks = async (id, ct) =>
        {
            var root = GameRoot;
            var environment = InstanceEnvironment.Resolve(root, id);
            var mods = Path.Combine(root, "instances", id, ".minecraft", "mods");
            var issues = await Task.Run(() => ModCompatibility.Inspect(Directory.Exists(mods) ? Directory.EnumerateFiles(mods) : [], environment.GameVersion, environment.LoaderSlug, environment.LoaderVersion, ct: ct), ct);
            if (issues.Any(i => i.IsError))
            {
                InspectionIssues.Clear(); foreach (var issue in issues) InspectionIssues.Add(issue);
                IsDetailOpen = true; DetailTab = 4;
                StatusText = "启动前发现模组问题，请先查看日志与诊断。";
                return false;
            }
            return true;
        };
    }
    private void ResetPackContext()
    {
        if (_packRoot == GameRoot && _packInstance == SelectedInstance?.InstanceId) return;
        _packPreparation?.Cancel();
        // A running durable task owns its plan until it finishes.
        if (_packPlan is {} plan && !_tasks.List().Any(t => t.Request.Arguments.GetValueOrDefault("plan") == Path.Combine(plan.Stage, "plan.json") && t.State is DurableTaskState.Ready or DurableTaskState.Running or DurableTaskState.Paused or DurableTaskState.Failed))
            try { _packUpdates.Discard(plan); } catch (IOException) { }
        _packRoot = GameRoot; _packInstance = SelectedInstance?.InstanceId;
        _packPlan = null; PackChanges.Clear(); PackReleases.Clear(); _allPackReleases = []; SelectedPackRelease = null;
        PackHint = "检查平台版本，或选择本地整合包，先预览差异再更新。";
        NotifyPackPlan(); RefreshPackRecovery(); OnPropertyChanged(nameof(PackIdentity));
    }
    private void NotifyPackPlan() { OnPropertyChanged(nameof(HasPackPlan)); OnPropertyChanged(nameof(PackPlanSummary)); }
    private void RefreshPackRecovery()
    {
        try { HasPackRecovery = SelectedInstance is {} i && _packUpdates.Recoveries(GameRoot, i.InstanceId).Count > 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { HasPackRecovery = false; }
        OnPropertyChanged(nameof(HasPackRecovery));
    }
    partial void OnPackChannelChanged(string value) => FilterPackReleases();
    private void FilterPackReleases()
    {
        PackReleases.Clear(); foreach (var release in _allPackReleases.Where(r => PackChannel == "全部版本" || r.Channel == PackChannel)) PackReleases.Add(release);
        SelectedPackRelease = PackReleases.FirstOrDefault();
    }
    [RelayCommand] private async Task CheckPackVersionsAsync()
    {
        if (!CanManagePack || SelectedInstance is not {} instance) return;
        var root = GameRoot; PackBusy = true;
        try
        {
            var origin = ModpackBaseline.Read(Path.Combine(root, "instances", instance.InstanceId))?.Origin;
            if (origin is null) { PackHint = "该实例尚未记录平台项目来源，请先选择本地更新包；之后可继续使用文件差异与恢复功能。"; return; }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var releases = await Download.Modpacks.GetReleasesAsync(origin, timeout.Token);
            if (root != GameRoot || SelectedInstance?.InstanceId != instance.InstanceId) return;
            _allPackReleases = releases; FilterPackReleases();
            PackHint = releases.Count == 0 ? "平台暂未返回可用版本。" : "已获取平台版本；正式版、测试版、开发版可分别筛选。";
        }
        catch (Exception ex) { PackHint = "检查版本失败：" + ex.Message; }
        finally { PackBusy = false; }
    }
    [RelayCommand] private async Task PrepareOnlinePackUpdateAsync()
    {
        if (SelectedPackRelease is {} release) await PreparePackUpdateFileAsync(null, release.Origin);
    }
    [RelayCommand] private async Task PrepareLocalPackUpdateAsync()
    {
        if (!CanManagePack) return;
        var path = await PlatformUi.PickFileAsync("选择新版整合包", "整合包|*.mrpack;*.zip");
        if (path is not null) await PreparePackUpdateFileAsync(path);
    }
    public async Task PreparePackUpdateFileAsync(string? path, ModpackOrigin? origin = null)
    {
        if (!CanManagePack || SelectedInstance is not {} instance) return;
        if (_packPlan is {} existing && PackPlanHasTask(existing)) { PackHint = "此预览已有未完成任务，请在下载任务中恢复，或取消并清理后重新准备。"; return; }
        var root = GameRoot;
        try { EnsureInstanceStopped(instance.InstanceId); } catch (Exception ex) { PackHint = ex.Message; return; }
        using var operation = GameOperationCoordinator.Shared.TryEnter(root, "准备整合包更新");
        if (operation is null) { PackHint = GameOperationCoordinator.Shared.BusyMessage(root); return; }
        PackBusy = true; IsBusy = true; using var cts = new CancellationTokenSource(); _packPreparation = cts;
        var old = _packPlan; _packPlan = null; PackChanges.Clear(); NotifyPackPlan();
        try
        {
            if (old is not null) _packUpdates.Discard(old);
            path ??= await Download.Modpacks.DownloadReleaseAsync(root, origin ?? throw new InvalidOperationException("请选择更新版本。"), cts.Token);
            await using var source = _modpackFactory.Open(path);
            var progress = new Progress<Launcher.Application.Abstractions.ModpackImportProgress>(p => { if (root == GameRoot && SelectedInstance?.InstanceId == instance.InstanceId) PackHint = p.Stage + " · " + p.Detail; });
            var prepared = await Task.Run(() => _packUpdates.PrepareAsync(source, root, instance.InstanceId, origin, progress, cts.Token), cts.Token);
            if (root != GameRoot || SelectedInstance?.InstanceId != instance.InstanceId) { _packUpdates.Discard(prepared); return; }
            _packPlan = prepared;
            foreach (var change in prepared.Changes.Where(c => c.Kind != "不变")) PackChanges.Add(new(change));
            PackHint = prepared.HasBaseline ? "勾选要应用的变更；冲突默认保留本地。应用前会自动备份所有将被修改的文件。" : "旧实例没有原始清单：重名文件默认保留本地，不会自动删除旧文件。请核对模组差异。";
            NotifyPackPlan();
        }
        catch (OperationCanceledException) { PackHint = "已取消准备更新；当前整合包保持原样。"; }
        catch (Exception ex) { PackHint = "准备更新失败：" + ex.Message; }
        finally { _packPreparation = null; PackBusy = false; IsBusy = false; }
    }
    [RelayCommand] private void CancelPackPreparation() => _packPreparation?.Cancel();
    [RelayCommand] private void ApplyPackUpdate()
    {
        if (!CanManagePack || _packPlan is not {} plan) return;
        if (PackPlanHasTask(plan)) { PackHint = "此预览已有任务，请在下载任务中恢复或重试。"; return; }
        var choices = PackChanges.ToDictionary(c => c.Path, c => c.Apply);
        AskConfirm("应用整合包更新", "将备份并应用已勾选的文件变更。存档与截图保持原样；首次启动新版世界前，建议另行备份存档。", plan.Summary, "备份并更新", () => _ = ApplyPackUpdateCoreAsync(plan, choices));
    }
    private void RefreshPackTaskState()
    {
        if (_packPlan is not {} plan) return;
        var task = _tasks.List().FirstOrDefault(t => t.Request.Arguments.GetValueOrDefault("plan") == Path.Combine(plan.Stage,"plan.json"));
        if (task?.State is not (DurableTaskState.Completed or DurableTaskState.Canceled)) return;
        _packPlan = null; PackChanges.Clear(); NotifyPackPlan(); RefreshPackRecovery();
        PackHint = task.State == DurableTaskState.Completed ? "更新完成，已创建更新前恢复点。" : "更新任务已取消；可以重新准备预览。";
    }
    private async Task ApplyPackUpdateCoreAsync(PackUpdatePlan plan, Dictionary<string, bool> choices)
    {
        var result = await QueueExperienceTaskAsync(new("pack-update", "更新整合包：" + plan.Name, plan.GameRoot, plan.InstanceId,
            new() { ["plan"] = Path.Combine(plan.Stage, "plan.json"), ["choices"] = JsonSerializer.Serialize(choices) }));
        if (result?.State == DurableTaskState.Completed)
        {
            _packPlan = null; PackChanges.Clear(); NotifyPackPlan(); PackHint = "更新完成，已创建更新前恢复点。";
            if (GameRoot == plan.GameRoot) { RefreshInstances(); SelectedInstance = Instances.FirstOrDefault(i => i.InstanceId == plan.InstanceId); DetailTab = 5; }
            RefreshPackRecovery();
        }
    }
    [RelayCommand] private void RestorePackUpdate()
    {
        if (!CanManagePack || SelectedInstance is not {} instance) return;
        var root = GameRoot;
        var recovery = _packUpdates.Recoveries(root, instance.InstanceId).FirstOrDefault(); if (recovery is null) return;
        AskConfirm("恢复更新前的整合包", "恢复上次整合包升级所修改的文件与版本信息。更新后对这些文件的修改也会被替换；存档与截图不变。", recovery.Name, "恢复更新前", () => _ = QueueExperienceTaskAsync(new("pack-restore", "恢复整合包更新", root, instance.InstanceId, new() { ["recovery"] = recovery.Id })));
    }
}
