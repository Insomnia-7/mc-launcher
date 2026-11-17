using Avalonia.Controls;
using Avalonia.VisualTree;
using Launcher.App.ViewModels;
using Launcher.Application.Experience;
using Launcher.Application.Install;
using Launcher.Application.Mods;

internal static class InstanceStorageUiChecks
{
    public static void Run(MainViewModel model,Window window,WorkbenchFixture fixture,Action<int> pump,Action<string,bool> check,Action<string,bool> capture)
    {
        void Wait(Task task){var clock=System.Diagnostics.Stopwatch.StartNew();while(!task.IsCompleted&&clock.ElapsedMilliseconds<15000)pump(10);if(!task.IsCompleted)throw new TimeoutException("实例管理检查超时");task.GetAwaiter().GetResult();pump(120);}
        void Until(Func<bool> done){var clock=System.Diagnostics.Stopwatch.StartNew();while(!done()&&clock.ElapsedMilliseconds<15000)pump(10);if(!done())throw new TimeoutException("实例管理状态超时");pump(100);}
        T Find<T>(string name)where T:Control=>window.GetVisualDescendants().OfType<T>().Single(c=>c.Name==name);
        const string id="fabric-loader-0.16.9-1.21.4";WorkbenchFixture.MakeInstance(model.GameRoot,id);
        Wait(fixture.InstallOldAsync(model.GameRoot,id,"alpha","alpha-1.jar","alpha-2"));
        Wait(fixture.InstallOldAsync(model.GameRoot,id,"beta","beta-1.jar","beta-2"));
        model.TryUseGameRoot(model.GameRoot,out _);model.SelectedInstance=model.Instances.Single(x=>x.InstanceId==id);
        model.OpenGameCommand.Execute(null);model.DetailTab=3;pump(150);
        var slider=Find<Slider>("MemorySlider");
        check("内存设置以滑块代替数值输入",!window.GetVisualDescendants().OfType<NumericUpDown>().Any(x=>x.Name=="MemoryInput")&&slider.TickFrequency==512&&slider.IsSnapToTickEnabled);
        check("新实例保持自动分配且禁用手动拖动",model.AutomaticMemory&&!slider.IsEnabled);
        model.SelectMemoryModeCommand.Execute("manual");slider.Value=3584;pump(50);
        check("自定义滑块即时反映 3.5GB",!model.AutomaticMemory&&model.MemoryMb==3584&&model.AllocatedMemoryText=="3.5 GB");
        check("内存分段条显示系统游戏剩余三段",Find<Grid>("MemoryBar").ColumnDefinitions.Count==3&&model.SystemMemoryText.Contains("GB /")&&model.RemainingMemoryText.Contains("GB"));
        model.SaveSettingsCommand.Execute(null);var saved=InstanceSettings.Load(model.GameRoot,id);
        check("保存并重载自定义内存模式",saved is {MemoryMb:3584,AutomaticMemory:false});
        capture("memory-manual",true);
        model.SelectMemoryModeCommand.Execute("auto");model.SaveSettingsCommand.Execute(null);
        check("可切回自动分配且模式持久化",InstanceSettings.Load(model.GameRoot,id).AutomaticMemory&&model.MemoryMb==InstanceSettings.AdaptiveMemoryMb());
        capture("memory-automatic",true);
        model.ShowInstancesCommand.Execute(null);pump(100);
        check("实例页提供直接管理模组入口",Find<Button>("ManageSelectedModsButton").Command==model.OpenSelectedModsCommand);
        model.OpenSelectedModsCommand.Execute(null);Wait(model.RefreshModsCommand.ExecuteAsync(null));
        check("模组管理显示每个文件的独立操作",model.ManagedMods.Count==2&&model.ManagedMods.All(x=>x.DetailsCommand is not null&&x.RevealCommand is not null&&x.ToggleCommand is not null&&x.DeleteCommand is not null));
        model.ManagedModQuery="alpha";pump(50);
        check("模组搜索按当前实例文件过滤",model.VisibleManagedMods.Count==1&&model.VisibleManagedMods[0].FileName=="alpha-1.jar");
        model.SelectAllManagedModsCommand.Execute(null);
        check("搜索全选只选当前结果",model.ManagedMods.Count(x=>x.Selected)==1);
        var alpha=model.VisibleManagedMods.Single();var betaFile=Path.Combine(ModChangePlans.ModsDirectory(model.GameRoot,id),"beta-1.jar");var betaBytes=File.ReadAllBytes(betaFile);
        Wait(model.ShowManagedModDetailsCommand.ExecuteAsync(alpha));
        check("单模组详情包含路径大小时间与哈希",model.HasManagedModDetails&&model.ManagedModDetails.Contains(alpha.FileName)&&model.ManagedModDetails.Contains("SHA-1")&&model.ManagedModDetails.Contains("修改：")&&model.ManagedModDetails.Contains("字节"));
        var location=MainViewModel.ModFileLocation(model.ManagedModPath(alpha));
        check("打开文件位置定位到选中文件",OperatingSystem.IsWindows()?location.FileName=="explorer.exe"&&location.ArgumentList.Single().StartsWith("/select,"):location.FileName==Path.GetDirectoryName(alpha.Mod.FullPath));
        capture("mod-file-details",true);
        var snapshots=new InstanceSnapshots(Launcher.App.AppPaths.DataDirectory,model.GameRoot,id);var before=snapshots.List().Count;
        Wait(model.ToggleManagedModCommand.ExecuteAsync(alpha));
        check("单独停用只改选中的模组",File.Exists(Path.Combine(ModChangePlans.ModsDirectory(model.GameRoot,id),"alpha-1.jar.disabled"))&&File.ReadAllBytes(betaFile).SequenceEqual(betaBytes));
        check("启停不重复备份整个整合包",snapshots.List().Count==before);
        alpha=model.ManagedMods.Single(x=>x.FileName.StartsWith("alpha"));Wait(model.ToggleManagedModCommand.ExecuteAsync(alpha));
        check("停用模组可重新启用",File.Exists(Path.Combine(ModChangePlans.ModsDirectory(model.GameRoot,id),"alpha-1.jar")));
        alpha=model.ManagedMods.Single(x=>x.FileName.StartsWith("alpha"));Wait(model.DeleteManagedModCommand.ExecuteAsync(alpha));
        check("单模组删除明确确认文件与永久删除",model.IsConfirmOpen&&model.ConfirmMessage.Contains("永久删除")&&model.ConfirmDetail.Contains("alpha-1.jar"));
        model.ConfirmCancelCommand.Execute(null);check("取消删除保留模组文件",File.Exists(alpha.Mod.FullPath));
        Wait(model.DeleteManagedModCommand.ExecuteAsync(alpha));model.ConfirmOkCommand.Execute(null);
        Until(()=>!model.IsBusy&&!model.ManagedMods.Any(x=>x.FileName.StartsWith("alpha")));
        check("确认后仅删除该模组且不复制整包",!File.Exists(alpha.Mod.FullPath)&&File.ReadAllBytes(betaFile).SequenceEqual(betaBytes)&&snapshots.List().Count==before);
        check("连续单文件操作不会用成功提示遮挡按钮",model.Toasts.Items.Count==0);
        model.ManagedModQuery="";pump(60);check("搜索复位显示剩余模组",model.VisibleManagedMods.Count==1&&model.VisibleManagedMods[0].FileName=="beta-1.jar");
        var stale=false;try{model.ManagedModPath(alpha);}catch(InvalidOperationException){stale=true;}
        check("过期模组行不能操作其他实例文件",stale);
        capture("mod-per-file-controls",true);
        for(var i=0;i<120;i++)model.InstalledMods.Add(new($"fixture-{i}.jar",$"列表夹具 {i}","1.0 · fabric",true,"fixture-"+i,Path.Combine(ModChangePlans.ModsDirectory(model.GameRoot,id),$"fixture-{i}.jar")));
        pump(150);var list=Find<ListBox>("ManagedModsList");var scroll=list.GetVisualDescendants().OfType<ScrollViewer>().Single();
        check("长模组列表具有独立滚动视口",scroll.Extent.Height>scroll.Viewport.Height&&scroll.Viewport.Height>0);
        scroll.Offset=new Avalonia.Vector(0,scroll.Extent.Height);pump(120);
        check("滚动能到达列表底部",scroll.Offset.Y>0);
        check("长列表仅创建可见行以避免卡顿",list.GetVisualDescendants().OfType<ListBoxItem>().Count()<30);
        model.ManagedModQuery="beta";pump(100);window.Width=900;window.Height=640;model.SelectedTheme=model.ThemeChoices.Single(t=>t.Id=="aurora");pump(120);
        capture("mod-controls-dark-small",true);
        check("小窗口暗色主题仍显示全部单行操作",model.VisibleManagedMods.Count==1&&list.Bounds.Width>350);
    }
}
