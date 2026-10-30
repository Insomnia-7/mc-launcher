using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Launcher.App;
using Launcher.App.ViewModels;
using Launcher.Desktop;
using Microsoft.Extensions.DependencyInjection;

internal static class WorkspaceUiChecks
{
    public static void Run(MainViewModel model,Window window,Action<int> pump,Action<string,bool> check,Action<string,bool> capture,Action<Control> click,string root)
    {
        T Find<T>(string name)where T:Control=>window.GetVisualDescendants().OfType<T>().Single(c=>c.Name==name);
        model.ShowInstancesCommand.Execute(null);model.SelectedTheme=model.ThemeChoices.Single(t=>t.Id=="glacier");window.Width=1180;window.Height=800;pump(120);
        check("主导航分为游戏库资源角色任务设置",new[]{"LibraryNav","DiscoverNav","WardrobeEntry","TaskCenterEntry","SettingsEntry"}.All(n=>Find<Button>(n).IsEffectivelyVisible)&&model.IsLibraryPage);
        check("游戏库集中展示所有实例",model.LibraryEntries.Count==model.Instances.Count&&Find<ItemsControl>("LibraryCards").IsEffectivelyVisible);
        model.LibraryQuery="no-such-game-98765";pump(40);check("搜索无匹配保留明确空状态",model.LibraryHasNoResults&&model.LibraryEntries.Count==0);
        model.LibraryQuery="1.21.4";pump(40);check("游戏库可按游戏版本搜索",model.LibraryEntries.Any(e=>e.Instance.BaseVersion=="1.21.4")&&model.LibraryEntries.All(e=>(e.Name+e.Meta).Contains("1.21.4")));
        model.LibraryQuery="";var entry=model.LibraryEntries.First(e=>e.Instance.VersionId=="1.21.4");var instance=entry.Instance;
        model.FavoriteGameCommand.Execute(entry);model.LibraryFavoritesOnly=true;pump(40);check("收藏筛选只显示收藏游戏",model.LibraryEntries.Count==1&&model.LibraryEntries[0].Instance.InstanceId==instance.InstanceId);
        model.LibraryFavoritesOnly=false;
        model.OpenGameCommand.Execute(model.LibraryEntries.First(e=>e.Instance.InstanceId==instance.InstanceId));pump(100);
        check("进入游戏详情保持实例与主操作一致",model.IsDetailOpen&&model.SelectedInstance?.InstanceId==instance.InstanceId&&Find<Button>("DetailLaunchButton").Command==model.LaunchCommand&&!model.HasOverlay);
        model.GameDisplayName="方块远行";model.GameGroupName="生存";model.SaveGamePresentationCommand.Execute(null);pump(40);
        check("显示名称与分组不改动游戏身份",model.SelectedGameName=="方块远行"&&model.SelectedInstance?.InstanceId==instance.InstanceId&&model.SelectedInstance.VersionId==instance.VersionId&&model.LibraryGroups.Contains("生存"));
        check("详情标题同步显示自定义名称",Find<TextBlock>("GameDetailTitle").Text=="方块远行");
        var file=Path.Combine(root,"library-cover.png");File.WriteAllBytes(file,AppearanceFixture.MakeSkin());model.ImportGameCoverFileAsync(file).GetAwaiter().GetResult();pump(40);
        check("封面复制到启动器数据并能显示",model.HasGameCover&&model.LibraryEntries.Any(e=>e.Instance.InstanceId==instance.InstanceId&&e.HasCover));
        File.Delete(file);check("删除源图片不影响已导入封面",model.HasGameCover);
        var bad=Path.Combine(root,"bad-cover.png");File.WriteAllText(bad,"not a png");model.ImportGameCoverFileAsync(bad).GetAwaiter().GetResult();
        check("无效封面保留原图并提示失败",model.HasGameCover&&model.WorkspaceHint.Contains("失败"));
        click(Find<Button>("ModsDetailTab"));check("模组页使用所选游戏的管理命令",model.IsModsDetailTab&&window.GetVisualDescendants().OfType<Button>().Any(b=>b.IsEffectivelyVisible&&ReferenceEquals(b.Command,model.RefreshModsCommand)));
        click(Find<Button>("BackupsDetailTab"));check("备份归属当前游戏且创建入口可见",model.IsBackupsDetailTab&&Find<Button>("CreateSnapshotButton").IsEffectivelyVisible);
        click(Find<Button>("SettingsDetailTab"));model.MemoryMb=4096;click(Find<Button>("SaveSettingsButton"));
        check("独立详情设置保存到对应实例",Launcher.Application.Install.InstanceSettings.Load(model.GameRoot,instance.InstanceId).MemoryMb==4096);
        click(Find<Button>("DiagnosticsDetailTab"));check("诊断移动到游戏详情",model.IsDiagnosticsDetailTab&&Find<Button>("InspectInstanceButton").IsEffectivelyVisible);
        click(Find<Button>("OverviewDetailTab"));model.RemoveGameCoverCommand.Execute(null);pump(40);capture("workspace-game-overview",true);
        check("恢复默认封面后使用原有彩色图案",!model.HasGameCover);
        window.Width=900;window.Height=640;pump(60);
        check("最小窗口详情启动入口可见",Find<Button>("DetailLaunchButton").TranslatePoint(new Point(),window) is {} p&&p.X>=0&&p.X+Find<Button>("DetailLaunchButton").Bounds.Width<=window.ClientSize.Width);
        capture("workspace-game-compact",true);window.Width=1180;window.Height=800;
        click(Find<Button>("BackToLibraryButton"));check("返回游戏库不残留实例详情状态",model.ShellPage=="library"&&!model.IsDetailOpen);
        model.LibraryGroup="生存";pump(50);check("分组筛选使用保存的实例分组",model.LibraryEntries.Count==1&&model.LibraryEntries[0].Name=="方块远行");model.LibraryGroup="全部分组";
        capture("workspace-library-cards",true);
        click(Find<Button>("ListLayoutButton"));check("游戏库可切换紧凑列表",model.LibraryCompact&&Find<ListBox>("LibraryList").IsEffectivelyVisible);
        capture("workspace-library-list",true);
        using(var services=LauncherServices.Create())
        {
            var restored=services.GetRequiredService<MainViewModel>();pump(120);var saved=restored.LibraryEntries.Single(e=>e.Instance.InstanceId==instance.InstanceId);
            check("重启恢复名称分组收藏与布局",restored.LibraryCompact&&saved.Name=="方块远行"&&saved.Group=="生存"&&saved.Favorite);
        }
        var originalRoot=model.GameRoot;var otherRoot=Path.Combine(root,"other-game-root");var version=Path.Combine(otherRoot,"versions","1.21.4");Directory.CreateDirectory(version);
        foreach(var suffix in new[]{".json",".jar"})File.Copy(Path.Combine(originalRoot,"versions","1.21.4","1.21.4"+suffix),Path.Combine(version,"1.21.4"+suffix));
        check("可切换到另一测试游戏目录",model.TryUseGameRoot(otherRoot,out _));pump(120);
        check("不同根目录不串用显示名称与收藏",model.LibraryEntries.Count==1&&model.LibraryEntries[0].Name!="方块远行"&&!model.LibraryEntries[0].Favorite&&!model.LibraryCompact);
        check("可切回原游戏目录",model.TryUseGameRoot(originalRoot,out _));pump(120);
        check("切回根目录恢复对应游戏库偏好",model.LibraryCompact&&model.LibraryEntries.Single(e=>e.Instance.InstanceId==instance.InstanceId).Name=="方块远行");
        model.DiagnosisText="另一游戏的启动错误";model.InspectionIssues.Add(new("另一游戏的体检结果","旧实例","旧建议"));
        model.SelectedInstance=model.Instances.First(i=>i.InstanceId!=instance.InstanceId);
        check("更换游戏清除上一实例的诊断信息",model.DiagnosisText.Length==0&&model.InspectionIssues.Count==0);
        model.SelectedInstance=model.Instances.First(i=>i.InstanceId==instance.InstanceId);
        model.SetLibraryLayoutCommand.Execute("cards");
        model.OpenTaskCenterCommand.Execute(null);pump(60);check("下载任务独立成页且没有实例诊断混入",model.IsTaskPage&&!model.HasOverlay&&Find<ItemsControl>("DurableTasksList").IsEffectivelyVisible&&!window.GetVisualDescendants().OfType<Button>().Any(b=>b.Name=="InspectInstanceButton"&&b.IsEffectivelyVisible));
        model.OpenAppearanceCommand.Execute(null);pump(60);check("设置独立成页并退出角色任务状态",model.IsSettingsPage&&!model.IsExperienceOpen&&!model.HasOverlay&&Find<Border>("PreferencesSurface").IsEffectivelyVisible);
        model.OpenAccountsCommand.Execute(null);model.CloseTopOverlayCommand.Execute(null);check("关闭账号浮层保留底层设置页",!model.IsAccountsOpen&&model.IsSettingsPage);
        model.ShowInstancesCommand.Execute(null);pump(80);check("主导航能从设置直接返回游戏库",model.ShellPage=="library"&&!model.IsPreferencesOpen);
        model.SelectedTheme=model.ThemeChoices.Single(t=>t.Id=="aurora");capture("workspace-library-dark",true);model.SelectedTheme=model.ThemeChoices.Single(t=>t.Id=="glacier");
    }
}
