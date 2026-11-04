using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.Application.Experience;
using Launcher.Application.Instances;
using Launcher.Application.Operations;

namespace Launcher.App.ViewModels;

public sealed partial class MainViewModel
{
    private CancellationTokenSource? _librarySearchCts;
    private CancellationTokenSource? _locationCopyCts;
    [ObservableProperty] private bool _showsLibraryDiscovery;
    [ObservableProperty] private GameLibraryCandidate? _selectedLibraryCandidate;
    [ObservableProperty] private string? _externalVersion;
    [ObservableProperty] private string? _externalRuntime;
    [ObservableProperty] private string _libraryDiscoveryHint = "选择上级文件夹查找游戏库；查找不会修改来源。";
    [ObservableProperty] private bool _dataRestartRequired;
    [ObservableProperty] private string _dataMoveHint = "";
    partial void OnDataRestartRequiredChanged(bool value) => OnPropertyChanged(nameof(HasOverlay));
    public string DataRootPath => AppPaths.DataDirectory;
    public string LocationSummary => $"游戏：{GameRoot}\n启动器数据、Java、缓存与备份：{AppPaths.DataDirectory}";
    public ObservableCollection<GameLibraryCandidate> LibraryCandidates { get; } = [];
    public ObservableCollection<string> ExternalVersions { get; } = [];
    public ObservableCollection<string> ExternalRuntimes { get; } = [];
    [RelayCommand] private async Task CopyCurrentLibraryAsync()
    {
        if (!CanRunTool() || _tasks.List().Any(t => t.CanResume || t.CanPause || t.CanCancel))
        { StorageHint = "请先完成或取消未完成任务，再迁移游戏库。"; return; }
        lock (_running) if (_running.Count > 0) { StorageHint = "请先关闭正在运行的游戏。"; return; }
        if (!RefreshGameRootHealth()) return;
        var destination = await PlatformUi.PickFolderAsync("选择一个空文件夹接收当前游戏库");
        if (destination is null) return;
        var source = GameRoot;
        AskConfirm("复制并迁移当前游戏库", "复制游戏、模组、存档与配置，并关联现有备份和封面。校验后切换位置；原副本保留。", $"原位置：{source}\n目标：{destination}", "复制并切换", () => _ = CopyCurrentCoreAsync(source,destination));
    }
    private async Task CopyCurrentCoreAsync(string source,string destination)
    {
        if (!CanRunTool() || GameRoot != source || _tasks.List().Any(t => t.CanResume || t.CanPause || t.CanCancel)) return;
        lock (_running) if (_running.Count > 0) return;
        using var operation = GameOperationCoordinator.Shared.TryEnter(source,"迁移游戏库");
        if (operation is null) return;
        using var cts = _locationCopyCts = new(); IsBusy = true; IsToolBusy = true;
        try
        {
            await Task.Run(() => GameLibraryMigration.CopyAsync(source,destination,AppPaths.DataDirectory,new Progress<string>(name=>StorageHint="正在复制并校验："+name),cts.Token),cts.Token);
            if (!TryUseGameRoot(destination,out var error)) throw new IOException("副本已完成，但位置未切换："+error);
            StorageHint = "已迁移到："+destination+"。原副本仍保留在："+source;
        }
        catch (OperationCanceledException) { StorageHint="迁移已取消，临时副本已清理，原游戏库保持原状。"; }
        catch (Exception ex) { StorageHint=ex.Message; }
        finally { IsBusy=false; IsToolBusy=false; _locationCopyCts=null; }
    }
    public bool CanCopyExternalLibrary => !IsBusy && SelectedLibraryCandidate is { IsFangyu: false } && ExternalVersion is not null && ExternalRuntime is not null;
    public bool CanUseFoundLibrary => !IsBusy && SelectedLibraryCandidate is { IsFangyu: true };
    [RelayCommand] private void UsePortableGameRoot()
    {
        if (!CanRunTool()) return;
        var path = Path.Combine(AppContext.BaseDirectory, ".minecraft");
        AskConfirm("使用便携游戏库", "将游戏保存在程序旁，Java、备份与缓存仍使用下方列出的数据位置；需要时可单独迁移。", path, "确认游戏位置", () =>
        { if (!TryUseGameRoot(path, out var error)) StorageHint = error ?? "无法使用此目录。"; });
    }
    [RelayCommand] private async Task MoveLauncherDataAsync()
    {
        if (!CanRunTool() || DataRestartRequired) return;
        lock (_running) if (_running.Count > 0) { DataMoveHint = "请先关闭正在运行的游戏。"; return; }
        if (Environment.GetEnvironmentVariable("MCLAUNCHER_HOME") is { Length: > 0 })
        { DataMoveHint = "当前数据位置由 MCLAUNCHER_HOME 指定，请通过原启动方式修改该设置。"; return; }
        if (_tasks.List().Any(t => t.CanResume || t.CanPause || t.CanCancel))
        { DataMoveHint = "请先完成或取消未完成任务，再迁移启动器数据。"; return; }
        var folder = await PlatformUi.PickFolderAsync("选择空文件夹保存启动器数据（Java、缓存、备份）");
        if (folder is null) return;
        AskConfirm("复制并迁移启动器数据", "逐文件校验后保存下次启动的位置。旧数据保留；游戏库仍在原位置，账号密钥仍由操作系统托管。完成后请立即重启启动器。", $"原位置：{AppPaths.DataDirectory}\n目标：{folder}\n游戏库保持：{GameRoot}", "复制并设置新位置", () => _ = MoveDataCoreAsync(folder));
    }
    private async Task MoveDataCoreAsync(string folder)
    {
        if (!CanRunTool() || _tasks.List().Any(t => t.CanResume || t.CanPause || t.CanCancel)) return;
        lock (_running) if (_running.Count > 0) return;
        using var cts = _locationCopyCts = new(); IsBusy = true; IsToolBusy = true;
        try
        {
            // Fail before a long copy if the bootstrap settings cannot be written.
            var probe = AppPaths.DataRootSettingsFile + ".probe";
            File.WriteAllText(probe, ""); File.Delete(probe);
            var progress = new Progress<string>(name => DataMoveHint = "正在复制并校验：" + name);
            await Task.Run(() => DataLocationMigration.CopyAsync(AppPaths.DataDirectory, folder, GameRoot, progress, cts.Token), cts.Token);
            var temp = AppPaths.DataRootSettingsFile + ".tmp";
            File.WriteAllText(temp, Path.GetFullPath(folder)); File.Move(temp, AppPaths.DataRootSettingsFile, true);
            DataRestartRequired = true; DataMoveHint = "新数据位置已保存，旧副本保留。请立即退出并重新打开启动器；游戏库未搬移。";
        }
        catch (OperationCanceledException) { DataMoveHint = "迁移已取消，临时副本已清理，当前位置不变。"; }
        catch (Exception ex) { DataMoveHint = "迁移未切换位置：" + ex.Message; }
        finally { IsToolBusy = false; IsBusy = DataRestartRequired; _locationCopyCts = null; }
    }
    [RelayCommand] private void RestartForDataMove()
    {
        if (DataRestartRequired) PlatformUi.Shutdown();
    }
    partial void OnSelectedLibraryCandidateChanged(GameLibraryCandidate? value)
    {
        OnPropertyChanged(nameof(CanCopyExternalLibrary)); OnPropertyChanged(nameof(CanUseFoundLibrary));
        ExternalVersion = null; ExternalVersions.Clear(); ExternalRuntimes.Clear(); ExternalRuntime = null;
        if (value is null) return;
        foreach (var version in GameLibraryDiscovery.Versions(value.Path)) ExternalVersions.Add(version);
        ExternalVersion = ExternalVersions.FirstOrDefault();
    }
    partial void OnExternalVersionChanged(string? value)
    {
        ExternalRuntimes.Clear();
        if (SelectedLibraryCandidate is not {} library || value is null) return;
        ExternalRuntimes.Add(library.Path);
        var isolated = Path.Combine(library.Path, "versions", value);
        if (Directory.Exists(isolated)) ExternalRuntimes.Add(isolated);
        // Do not infer foreign launcher's isolation setting. The player reviews the real directory.
        ExternalRuntime = null;
        LibraryDiscoveryHint = "请选择原运行目录：里面应当能看到原来的 saves、mods 和 config。共享目录与版本隔离目录均列在下方。";
    }
    partial void OnExternalRuntimeChanged(string? value) => OnPropertyChanged(nameof(CanCopyExternalLibrary));
    [RelayCommand] private async Task SearchLibraryFolderAsync()
    {
        if (!CanRunTool()) return;
        var folder = await PlatformUi.PickFolderAsync("选择查找范围（最多向下 4 层）", GameRoot);
        if (folder is null) return;
        ShowsLibraryDiscovery = true;
        await FindLibrariesAsync([folder], 4);
    }
    private async Task FindLibrariesAsync(IEnumerable<string> roots, int depth)
    {
        _librarySearchCts?.Cancel(); _librarySearchCts?.Dispose();
        var cts = _librarySearchCts = new(); IsToolBusy = true;
        LibraryDiscoveryHint = "正在查找，只读取版本信息…";
        try
        {
            var candidates = await Task.Run(() => GameLibraryDiscovery.Scan(roots, depth, cts.Token), cts.Token);
            if (cts != _librarySearchCts) return;
            LibraryCandidates.Clear(); foreach (var item in candidates) LibraryCandidates.Add(item);
            SelectedLibraryCandidate = LibraryCandidates.FirstOrDefault();
            LibraryDiscoveryHint = candidates.Count == 0 ? "未找到可用版本。请选择更接近游戏的上级文件夹继续查找。" : $"找到 {candidates.Count} 个游戏库，尚未切换或修改任何目录。选择来源后，请核对原运行目录。";
        }
        catch (OperationCanceledException) { LibraryDiscoveryHint = "查找已取消，当前游戏目录保持原状。"; }
        catch (Exception ex) { LibraryDiscoveryHint = ex.Message; }
        finally { if (cts == _librarySearchCts) IsToolBusy = false; }
    }
    [RelayCommand] private void CancelLibrarySearch() => _librarySearchCts?.Cancel();
    [RelayCommand] private void CancelLocationCopy() => _locationCopyCts?.Cancel();
    [RelayCommand] private void OpenExternalRuntime()
    {
        if (ExternalRuntime is {} path) OpenStoragePath(path);
    }
    private void OpenStoragePath(string path)
    {
        try { if (Directory.Exists(path)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { StorageHint = ex.Message; }
    }
    [RelayCommand] private void OpenStorageArea(StorageArea? area) { if (area is not null) OpenStoragePath(area.Path); }
    [RelayCommand] private void ManageStoredGames() { ShowInstances(); }
    [RelayCommand] private void UseFoundLibrary()
    {
        if (!CanRunTool() || SelectedLibraryCandidate is not { IsFangyu: true } candidate) return;
        AskConfirm("切换方隅游戏库", "使用此游戏库，不搬移原有文件。", candidate.Path, "切换游戏库", () =>
        { if (!TryUseGameRoot(candidate.Path, out var error)) StorageHint = error ?? "无法切换。"; else StorageHint = LocationSummary; });
    }
    [RelayCommand] private async Task CopyExternalLibraryAsync()
    {
        if (!CanCopyExternalLibrary || !CanRunTool() || SelectedLibraryCandidate is not {} source || ExternalVersion is not {} version || ExternalRuntime is not {} runtime) return;
        var destination = await PlatformUi.PickFolderAsync("选择一个空文件夹接收游戏副本");
        if (destination is null) return;
        AskConfirm("复制导入外部游戏", "复制共享依赖和选中版本的存档、模组与配置，逐文件校验。来源保持原状；完成后切换到新库。", $"来源：{source.Path}\n版本：{version}\n原运行目录：{runtime}\n目标：{destination}", "开始复制", () => _ = CopyExternalCoreAsync(source.Path, version, runtime, destination));
    }
    private async Task CopyExternalCoreAsync(string source, string version, string runtime, string destination)
    {
        if (!CanRunTool()) return;
        using var operation = GameOperationCoordinator.Shared.TryEnter(destination, "复制导入");
        if (operation is null) return;
        using var cts = _locationCopyCts = new(); IsToolBusy = true; IsBusy = true;
        try
        {
            var progress = new Progress<string>(text => LibraryDiscoveryHint = "正在复制并校验：" + text);
            await Task.Run(() => ExternalGameImport.CopyAsync(source, version, runtime, destination, progress, cts.Token), cts.Token);
            if (!TryUseGameRoot(destination, out var error)) throw new IOException("副本已完成，但保存位置失败：" + error);
            LibraryDiscoveryHint = "导入完成；来源文件保持原状。已切换到：" + destination;
        }
        catch (OperationCanceledException) { LibraryDiscoveryHint = "复制已取消，临时副本已清理，来源保持原状。"; }
        catch (Exception ex) { LibraryDiscoveryHint = ex.Message; }
        finally { _locationCopyCts = null; IsToolBusy = false; IsBusy = false; }
    }
}
