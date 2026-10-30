using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Launcher.App;
using Launcher.App.ViewModels;
using Launcher.Application.Abstractions;
using Launcher.Application.Experience;
using Launcher.Application.Install;
using Launcher.Desktop;
using Microsoft.Extensions.DependencyInjection;

internal static class MaintenanceUiChecks
{
    public static void Run(Window window, Action<int> pump, Action<string, bool> check, Action<string, bool> capture)
    {
        void Until(Func<bool> done) { var clock = System.Diagnostics.Stopwatch.StartNew(); while (!done() && clock.ElapsedMilliseconds < 15000) pump(10); if (!done()) throw new TimeoutException("维护页面检查超时。"); pump(100); }
        void Wait(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
        T Find<T>(string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
        var original = window.DataContext; var fixture = new MaintenanceFixture(); fixture.SeedVersion(AppPaths.GameRoot);
        var world = MaintenanceFixture.SeedWorld(AppPaths.GameRoot, fixture.Version.Id, "home", "林间小屋");
        MaintenanceFixture.SeedWorld(AppPaths.GameRoot, fixture.Version.Id, "island", "海岛生存");
        using var services = LauncherServices.Create(s =>
        {
            s.AddSingleton<IVersionMetaClient>(fixture); s.AddSingleton<IDownloader>(fixture);
            s.AddSingleton(new GameInstaller(fixture, fixture));
            var appearance = new AppearanceFixture(); s.AddSingleton<Launcher.Application.Accounts.IPlayerAppearanceClient>(appearance); s.AddSingleton<Launcher.Application.Accounts.ISkinService>(appearance);
        });
        var model = services.GetRequiredService<MainViewModel>();
        try
        {
            window.DataContext = model; window.Width = 1180; window.Height = 800; pump(200);
            model.ShowInstancesCommand.Execute(null); model.SelectedInstance = model.Instances.Single(i => i.InstanceId == fixture.Version.Id);
            model.OpenInstanceHistoryCommand.Execute(null); Wait(model.RefreshWorldsCommand.ExecuteAsync(null));
            if (model.Worlds.Count != 2) Console.WriteLine($"World fixture: {model.GameRoot}, selected={model.SelectedInstance?.InstanceId}, hint={model.WorldHint}, worlds={string.Join(',', model.Worlds.Select(w => w.Name))}");
            check("存档中心读取两个世界与 NBT 信息", model.Worlds.Count == 2 && model.Worlds.Any(w => w.Name == "林间小屋" && w.Summary.Contains("创造")));
            check("世界列表与备份按钮绑定当前实例", Find<ListBox>("WorldList").ItemsSource == model.Worlds && Find<Button>("BackupWorldButton").Command == model.BackupWorldCommand);
            model.SelectedWorld = model.Worlds.Single(w => w.World.Folder == "home"); Wait(model.BackupWorldCommand.ExecuteAsync(null));
            check("界面单世界备份进入任务并刷新备份列表", model.WorldBackups.Count == 1 && model.DurableTasks.Any(t => t.Title.Contains("林间小屋") && t.StateText == "已完成"));
            capture("world-center-light", true);
            File.WriteAllText(Path.Combine(world, "region.mca"), "changed"); model.RestoreWorldCommand.Execute(null);
            check("恢复世界展示替换范围与自动备份提示", model.IsConfirmOpen && model.ConfirmMessage.Contains("自动保存") && model.ConfirmMessage.Contains("其他存档"));
            model.ConfirmCancelCommand.Execute(null); check("取消恢复保留世界", File.ReadAllText(Path.Combine(world, "region.mca")) == "changed");
            model.RestoreWorldCommand.Execute(null); model.ConfirmOkCommand.Execute(null);
            Until(() => !model.IsBusy && File.ReadAllText(Path.Combine(world, "region.mca")) == "original world"); Wait(model.RefreshWorldsCommand.ExecuteAsync(null));
            check("恢复世界自动增加恢复前备份", model.WorldBackups.Count == 2);
            model.DeleteWorldBackupCommand.Execute(null); check("删除世界备份也先确认", model.IsConfirmOpen); model.ConfirmCancelCommand.Execute(null);
            model.DetailTab = 4; Wait(model.InspectGameFilesCommand.ExecuteAsync(null));
            check("修复页面展示缺失文件与修复入口", model.HasRepairFiles && model.RepairFiles.Count == 1 && Find<Button>("RepairGameFilesButton").IsVisible);
            var repairButton = Find<Button>("RepairGameFilesButton");
            check("浅色主题修复按钮保持文字对比", repairButton.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "修复缺失或损坏文件" && t.Foreground?.ToString() == repairButton.Foreground?.ToString()) && model.RepairHint.Contains("3 个文件"));
            capture("targeted-repair", true);
            Wait(model.RepairGameFilesCommand.ExecuteAsync(null));
            check("界面修复通过任务仅下载缺失资源", !model.HasRepairFiles && fixture.Downloaded.Count == 1 && model.RepairHint.Contains("重新校验"));
            model.SelectedInstance = model.Instances.First(i => i.InstanceId != fixture.Version.Id); check("切换实例清除上个实例修复结果", !model.HasRepairFiles && model.RepairFiles.Count == 0);
            var cache = Path.Combine(AppPaths.DataDirectory, "cache/meta/maintenance-old.json"); Directory.CreateDirectory(Path.GetDirectoryName(cache)!); File.WriteAllText(cache, "cache"); File.SetLastWriteTimeUtc(cache, DateTime.UtcNow.AddDays(-8));
            Wait(model.OpenStorageSettingsCommand.ExecuteAsync(null));
            check("设置新增独立存储页并展示占用", model.IsSettingsPage && model.ShowsStorage && !model.IsNetworkSettings && model.StorageAreas.Any(a => a.Key == "cache/meta" && a.CanClean));
            check("存储扫描按钮与命令相连", Find<Button>("ScanStorageButton").Command == model.ScanStorageCommand);
            capture("storage-light", true);
            model.OpenNetworkCommand.Execute(null); check("存储切回网络即使网络标记已打开也生效", model.IsNetworkSettings && !model.ShowsStorage);
            Wait(model.OpenStorageSettingsCommand.ExecuteAsync(null)); model.OpenAppearanceCommand.Execute(null); check("存储切回外观只展示外观内容", model.IsAppearanceSettings && !model.ShowsStorage);
            Wait(model.OpenStorageSettingsCommand.ExecuteAsync(null));
            model.CleanStorageCommand.Execute(model.StorageAreas.Single(a => a.Key == "cache/meta")); check("清理缓存先显示分类确认", model.IsConfirmOpen && File.Exists(cache)); model.ConfirmOkCommand.Execute(null);
            if (model.DurableTasks.Any(t => t.CanResume))
            {
                check("存在未完成任务时阻止缓存清理", File.Exists(cache) && model.StorageHint.Contains("未完成"));
                foreach (var task in model.DurableTasks.Where(t => t.CanResume).ToArray()) model.CancelDurableTaskCommand.Execute(task);
                model.CleanStorageCommand.Execute(model.StorageAreas.Single(a => a.Key == "cache/meta")); model.ConfirmOkCommand.Execute(null);
            }
            Until(() => !model.StorageBusy); check("确认清理只删除陈旧缓存", !File.Exists(cache) && Directory.Exists(world));
            model.SelectedTheme = model.ThemeChoices.Single(t => t.Id == "aurora"); Wait(model.ScanStorageCommand.ExecuteAsync(null)); capture("storage-dark", true);
            window.Width = 900; window.Height = 640; pump(100); capture("storage-compact", true);
            check("最小窗口保留存储扫描按钮", Find<Button>("ScanStorageButton").TranslatePoint(default, window) is { X: >= 0, Y: >= 0 } point && point.X + Find<Button>("ScanStorageButton").Bounds.Width <= window.ClientSize.Width);
        }
        finally { window.DataContext = original; window.Width = 1180; window.Height = 800; }
    }
}
