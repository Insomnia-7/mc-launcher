using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.Application.Experience;
using Launcher.Application.Install;
using Launcher.Application.Operations;
using Launcher.App;

namespace Launcher.App.ViewModels;

public sealed class WorldRow(WorldEntry world)
{
    public WorldEntry World { get; } = world;
    public string Name => World.Name;
    public string Summary => World.Summary;
    public string Notice => World.Notice;
    public Bitmap? Icon { get; } = LoadIcon(world.IconPath);
    public bool HasIcon => Icon is not null;
    private static Bitmap? LoadIcon(string? path)
    { try { if (path is null) return null; ExperienceFiles.RejectLinks(path); using var file = File.OpenRead(path); return Bitmap.DecodeToWidth(file, 128); } catch (Exception) { return null; } }
}

public sealed partial class MainViewModel
{
    private int _worldRevision;
    private CancellationTokenSource? _worldScan;
    private string? _worldRoot;
    private string? _worldInstance;
    private string? _repairInstance;
    private string? _repairRoot;
    [ObservableProperty] private WorldRow? _selectedWorld;
    [ObservableProperty] private WorldBackup? _selectedWorldBackup;
    [ObservableProperty] private string _worldHint = "关闭游戏后，可以单独备份和恢复一个世界。";
    [ObservableProperty] private string _repairHint = "检查游戏本体、依赖库和资源文件，只补下载缺失或校验不符的文件。";
    [ObservableProperty] private bool _hasRepairFiles;
    [ObservableProperty] private bool _showsStorage;
    [ObservableProperty] private bool _storageBusy;
    [ObservableProperty] private string _storageHint = "扫描后显示缓存可回收量与游戏占用；扫描可能需要一些时间。";
    public bool IsNetworkSettings => IsNetworkOpen && !ShowsStorage && !ShowsLauncherUpdates;
    public bool IsAppearanceSettings => IsAppearanceOpen && !ShowsStorage && !ShowsLauncherUpdates;
    public bool CanManageStorage => !StorageBusy && CanEditExperience;
    public ObservableCollection<WorldRow> Worlds { get; } = [];
    public ObservableCollection<WorldBackup> WorldBackups { get; } = [];
    public ObservableCollection<RepairFile> RepairFiles { get; } = [];
    public ObservableCollection<StorageArea> StorageAreas { get; } = [];
    private void InitializeMaintenance()
    {
        BeforeNetworkSettings = () => { ShowsStorage = false; ShowsLauncherUpdates = false; };
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SelectedInstance) or nameof(GameRoot))
            {
                ResetWorldContext();
                if (_repairRoot != GameRoot || _repairInstance != SelectedInstance?.InstanceId)
                {
                    _repairRoot = _repairInstance = null; RepairFiles.Clear(); HasRepairFiles = false;
                    RepairHint = "检查游戏本体、依赖库和资源文件，只补下载缺失或校验不符的文件。";
                }
                if (e.PropertyName == nameof(GameRoot)) { StorageAreas.Clear(); StorageHint = "游戏目录已切换，请重新扫描存储占用。"; }
            }
            if (e.PropertyName is nameof(IsAppearanceOpen) or nameof(IsNetworkOpen))
            { ShowsStorage = false; ShowsLauncherUpdates = false; OnPropertyChanged(nameof(IsNetworkSettings)); OnPropertyChanged(nameof(IsAppearanceSettings)); }
            if (e.PropertyName is nameof(CanEditExperience) or nameof(StorageBusy)) OnPropertyChanged(nameof(CanManageStorage));
        };
    }
    partial void OnShowsStorageChanged(bool value) { OnPropertyChanged(nameof(IsNetworkSettings)); OnPropertyChanged(nameof(IsAppearanceSettings)); }
    partial void OnSelectedWorldChanged(WorldRow? value)
    {
        WorldBackups.Clear(); SelectedWorldBackup = null;
        try
        {
            if (value is null || SelectedInstance is not {} instance) return;
            foreach (var backup in new WorldCatalog(AppPaths.DataDirectory, GameRoot, instance.InstanceId).Backups(value.World.Folder)) WorldBackups.Add(backup);
            SelectedWorldBackup = WorldBackups.FirstOrDefault();
        }
        catch (Exception ex) { WorldHint = ex.Message; }
    }
    private void ResetWorldContext()
    {
        if (_worldRoot == GameRoot && _worldInstance == SelectedInstance?.InstanceId) return;
        _worldRoot = GameRoot; _worldInstance = SelectedInstance?.InstanceId;
        _worldRevision++; _worldScan?.Cancel();
        var old = Worlds.ToArray(); Worlds.Clear(); SelectedWorld = null; WorldBackups.Clear();
        foreach (var row in old) row.Icon?.Dispose();
    }
    [RelayCommand] private async Task RefreshWorldsAsync()
    {
        ResetWorldContext();
        var revision = ++_worldRevision; var root = GameRoot; var id = SelectedInstance?.InstanceId; var folder = SelectedWorld?.World.Folder;
        _worldScan?.Cancel(); _worldScan?.Dispose(); var scan = _worldScan = new CancellationTokenSource();
        var token = scan.Token;
        try
        {
            var worlds = id is null ? [] : await Task.Run(() => new WorldCatalog(AppPaths.DataDirectory, root, id).List(token), token);
            if (revision != _worldRevision || GameRoot != root || SelectedInstance?.InstanceId != id) return;
            var old = Worlds.ToArray(); Worlds.Clear(); SelectedWorld = null;
            foreach (var world in worlds) Worlds.Add(new(world));
            SelectedWorld = Worlds.FirstOrDefault(w => w.World.Folder == folder) ?? Worlds.FirstOrDefault();
            foreach (var row in old) row.Icon?.Dispose();
            WorldHint = Worlds.Count == 0 ? "还没有本地世界。进入游戏创建世界后，点击刷新。" : $"共 {Worlds.Count} 个世界；备份和恢复只影响选中的世界。";
        }
        catch (Exception ex) { if (revision == _worldRevision) WorldHint = ex.Message; }
    }
    [RelayCommand] private async Task BackupWorldAsync()
    {
        if (!CanEditExperience || SelectedWorld is not {} world || SelectedInstance is not {} instance) return;
        await QueueExperienceTaskAsync(new("world-backup", "备份世界：" + world.Name, GameRoot, instance.InstanceId, new() { ["folder"] = world.World.Folder, ["name"] = world.Name }));
        await RefreshWorldsAsync();
    }
    [RelayCommand] private void RestoreWorld()
    {
        if (!CanEditExperience || SelectedWorldBackup is not {} backup || SelectedInstance is not {} instance) return;
        var root = GameRoot;
        AskConfirm("恢复世界备份", "仅替换此世界；恢复前自动保存当前世界，其他存档不变。", backup.Name + "\n" + backup.Summary, "恢复此世界", () => _ = RestoreWorldCoreAsync(root, instance.InstanceId, backup.Id));
    }
    private async Task RestoreWorldCoreAsync(string root, string id, string backup)
    {
        await QueueExperienceTaskAsync(new("world-restore", "恢复世界备份", root, id, new() { ["backup"] = backup }));
        await RefreshWorldsAsync();
    }
    [RelayCommand] private void DeleteWorldBackup()
    {
        if (!CanEditExperience || SelectedWorldBackup is not {} backup || SelectedInstance is not {} instance) return;
        var root = GameRoot;
        AskConfirm("删除世界备份", "仅删除这一份备份，不删除当前世界。", backup.Name + "\n" + backup.Summary, "删除备份", () =>
        {
            try
            {
                if (!CanEditExperience) return;
                EnsureInstanceStopped(instance.InstanceId, root);
                using var operation = GameOperationCoordinator.Shared.TryEnter(root, "删除世界备份");
                if (operation is null) { WorldHint = GameOperationCoordinator.Shared.BusyMessage(root); return; }
                new WorldCatalog(AppPaths.DataDirectory, root, instance.InstanceId).DeleteBackup(backup.Id);
                _ = RefreshWorldsAsync();
            }
            catch (Exception ex) { WorldHint = ex.Message; }
        });
    }
    [RelayCommand] private void OpenWorldFolder()
    {
        try
        {
            if (SelectedWorld is not {} world || SelectedInstance is not {} instance) return;
            var path = new WorldCatalog(AppPaths.DataDirectory, GameRoot, instance.InstanceId).GetWorldPath(world.World.Folder);
            if (Directory.Exists(path)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { WorldHint = ex.Message; }
    }
    [RelayCommand] private async Task InspectGameFilesAsync()
    {
        if (!CanEditExperience || SelectedInstance is not {} instance) return;
        var root = GameRoot; var id = instance.InstanceId;
        await ExperienceActionAsync(async ct =>
        {
            RepairHint = "正在读取版本清单并校验文件，请稍候…";
            var version = await _versionLoader.LoadAsync(root, instance.VersionId, ct);
            var report = await Task.Run(() => _installer.InspectAsync(version, root, ct: ct), ct);
            if (GameRoot != root || SelectedInstance?.InstanceId != id) return;
            _repairRoot = root; _repairInstance = id; RepairFiles.Clear();
            foreach (var file in report.Files) RepairFiles.Add(file);
            HasRepairFiles = RepairFiles.Count > 0; RepairHint = report.Summary;
        });
        if (GameRoot == root && SelectedInstance?.InstanceId == id && RepairHint.StartsWith("正在")) RepairHint = ExperienceHint;
    }
    [RelayCommand] private async Task RepairGameFilesAsync()
    {
        if (!CanEditExperience || !HasRepairFiles || _repairRoot != GameRoot || _repairInstance != SelectedInstance?.InstanceId) return;
        var root = GameRoot; var instance = SelectedInstance!;
        var result = await QueueExperienceTaskAsync(new("repair", "定向修复：" + SelectedGameName, root, instance.InstanceId, new() { ["version"] = instance.VersionId }));
        if (GameRoot == root && SelectedInstance?.InstanceId == instance.InstanceId)
        {
            RepairFiles.Clear(); HasRepairFiles = false;
            RepairHint = result?.State == DurableTaskState.Completed ? "游戏依赖已修复并重新校验；模组、配置与存档保持原状。" : "任务未完成，可在下载任务中查看原因或恢复；修复前会重新检查文件。";
        }
    }
    [RelayCommand] private async Task OpenStorageSettingsAsync()
    { OpenNetwork(); ShowsStorage = true; await ScanStorageAsync(); }
    [RelayCommand] private async Task ScanStorageAsync()
    {
        if (!CanManageStorage) return;
        var root = GameRoot; StorageBusy = true; StorageHint = "正在统计文件占用…";
        try
        {
            var areas = await Task.Run(() => new StorageCatalog(AppPaths.DataDirectory, root).Scan());
            if (root != GameRoot) return;
            StorageAreas.Clear(); foreach (var area in areas) StorageAreas.Add(area);
            StorageHint = $"可回收缓存 {areas.Where(a => a.CanClean).Sum(a => a.Bytes) / 1048576d:F1} MB。缓存分类显示可清理部分；Java 总量包含安装包缓存，请勿重复相加。";
        }
        catch (Exception ex) { StorageHint = ex.Message; }
        finally { StorageBusy = false; }
    }
    [RelayCommand] private void CleanStorage(StorageArea? area)
    {
        if (!CanManageStorage || area?.CanClean != true) return;
        AskConfirm("清理缓存", "只删除该分类中超过 7 天的完整缓存；再次需要时自动下载。", area.Name + " · " + area.Summary, "清理缓存", () => _ = CleanStorageCoreAsync(area.Key, GameRoot));
    }
    private async Task CleanStorageCoreAsync(string key, string root)
    {
        if (!CanManageStorage || root != GameRoot) return;
        if (_tasks.List().Any(t => t.CanResume || t.CanPause)) { StorageHint = "存在未完成的任务，请先完成或取消任务，再清理缓存。"; return; }
        using var operation = GameOperationCoordinator.Shared.TryEnter(root, "清理缓存");
        if (operation is null) { StorageHint = GameOperationCoordinator.Shared.BusyMessage(root); return; }
        StorageBusy = true; IsBusy = true;
        try { var bytes = await Task.Run(() => new StorageCatalog(AppPaths.DataDirectory, root).Clean(key)); StorageHint = $"已释放 {bytes / 1048576d:F1} MB。可重新扫描查看占用。"; StorageAreas.Clear(); }
        catch (Exception ex) { StorageHint = ex.Message; }
        finally { IsBusy = false; StorageBusy = false; }
    }
}
