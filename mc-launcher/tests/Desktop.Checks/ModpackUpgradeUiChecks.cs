using System.IO.Compression;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Launcher.App.ViewModels;
using Launcher.Application.Experience;
using Launcher.Application.Install;
using Launcher.Domain.Content;

internal static class ModpackUpgradeUiChecks
{
    public static void Run(MainViewModel model, Window window, Action<int> pump, Action<string,bool> check, Action<string,bool> capture, string root)
    {
        void Wait(Task task) { var clock=System.Diagnostics.Stopwatch.StartNew();while(!task.IsCompleted&&clock.ElapsedMilliseconds<15000)pump(10);if(!task.IsCompleted)throw new TimeoutException("整合包操作超时");task.GetAwaiter().GetResult();pump(120); }
        void Until(Func<bool> done) {var clock=System.Diagnostics.Stopwatch.StartNew();while(!done()&&clock.ElapsedMilliseconds<15000)pump(10);if(!done())throw new TimeoutException("整合包状态超时");pump(100);}
        T Find<T>(string name) where T:Control=>window.GetVisualDescendants().OfType<T>().Single(c=>c.Name==name);
        const string id="pack-workspace";
        var instance=Path.Combine(model.GameRoot,"instances",id);var game=Path.Combine(instance,".minecraft");Directory.CreateDirectory(Path.Combine(game,"config"));
        File.WriteAllText(Path.Combine(game,"config/settings.json"),"{\"local\":1,\"upstream\":1}");File.WriteAllText(Path.Combine(game,"config/theme.toml"),"theme=old");
        new InstanceMetadata("山野来信","1.0","Modrinth",null,"1.21.4",null,"1.21.4").Write(model.GameRoot,id);
        var baseline=ModpackBaseline.CaptureAsync(game,"山野来信","1.0","Modrinth").GetAwaiter().GetResult();ExperienceFiles.Write(Path.Combine(instance,ModpackBaseline.FileName),baseline);
        File.WriteAllText(Path.Combine(game,"config/settings.json"),"{\"local\":2,\"upstream\":1}");File.WriteAllText(Path.Combine(game,"config/theme.toml"),"theme=mine");
        var archive=Path.Combine(root,"山野来信-1.1.mrpack");
        using(var zip=ZipFile.Open(archive,ZipArchiveMode.Create))
        {
            using(var writer=new StreamWriter(zip.CreateEntry("modrinth.index.json").Open()))writer.Write("""{"formatVersion":1,"game":"minecraft","name":"山野来信","versionId":"1.1","files":[],"dependencies":{"minecraft":"1.21.4"}}""");
            using(var writer=new StreamWriter(zip.CreateEntry("overrides/config/settings.json").Open()))writer.Write("{\"local\":1,\"upstream\":2}");
            using(var writer=new StreamWriter(zip.CreateEntry("overrides/config/theme.toml").Open()))writer.Write("theme=new");
            using(var writer=new StreamWriter(zip.CreateEntry("overrides/config/new.json").Open()))writer.Write("{}");
        }
        model.TryUseGameRoot(model.GameRoot,out _);model.ShowInstancesCommand.Execute(null);model.SelectedInstance=model.Instances.Single(i=>i.InstanceId==id);model.OpenGameCommand.Execute(null);
        model.SelectDetailTabCommand.Execute("5");pump(100);
        check("整合包有独立更新工作区",model.IsPackDetailTab&&Find<Button>("CheckPackVersionsButton").IsEffectivelyVisible);
        Wait(model.PreparePackUpdateFileAsync(archive));
        check("界面展示文件差异和冲突默认选择",model.HasPackPlan&&model.PackChanges.Count==3&&model.PackChanges.Single(c=>c.Kind=="冲突").Apply==false);
        capture("pack-upgrade-light",true);
        model.SelectedTheme=model.ThemeChoices.Single(t=>t.Id=="aurora");pump(100);capture("pack-upgrade-dark",true);
        model.SelectedTheme=model.ThemeChoices.Single(t=>t.Id=="glacier");
        model.ApplyPackUpdateCommand.Execute(null);pump(50);check("整合包更新先展示确认摘要",model.IsConfirmOpen);model.ConfirmCancelCommand.Execute(null);
        check("取消确认不改变整合包",InstanceMetadata.TryRead(model.GameRoot,id)?.PackVersion=="1.0");
        model.ApplyPackUpdateCommand.Execute(null);model.ConfirmOkCommand.Execute(null);Until(()=>!model.IsBusy&&InstanceMetadata.TryRead(model.GameRoot,id)?.PackVersion=="1.1");
        check("界面可应用整合包升级并显示恢复入口",model.HasPackRecovery&&File.ReadAllText(Path.Combine(game,"config/theme.toml"))=="theme=mine");
        model.RestorePackUpdateCommand.Execute(null);model.ConfirmOkCommand.Execute(null);Until(()=>!model.IsBusy&&InstanceMetadata.TryRead(model.GameRoot,id)?.PackVersion=="1.0");
        check("界面可恢复更新前版本和文件",!File.Exists(Path.Combine(game,"config/new.json"))&&File.ReadAllText(Path.Combine(game,"config/settings.json")).Contains("\"local\":2"));
        model.OpenTaskCenterCommand.Execute(null);pump(100);check("任务中心提供队列自动接续开关",Find<CheckBox>("AutoContinueDownloadsChoice").IsEffectivelyVisible);
        model.ShowDownloadsCommand.Execute(null);var download=model.Download;download.SelectedSource=DownloadViewModel.ModrinthSource;download.SelectedCategory=download.Categories.First(c=>c.Category==DownloadCategory.Modpack&&!c.IsSeparator);
        Wait(download.SearchCommand.ExecuteAsync(null));Wait(download.OpenResourceCommand.ExecuteAsync(download.Results.First()));
        check("整合包封面与介绍作为默认详情",download.ShowsPackOverview&&Find<Border>("PackResourceHero").IsEffectivelyVisible&&Find<ScrollViewer>("PackOverviewScroll").IsEffectivelyVisible);
        capture("pack-discover-light",true);model.SelectedTheme=model.ThemeChoices.Single(t=>t.Id=="aurora");pump(100);capture("pack-discover-dark",true);
        download.ShowPackVersionsCommand.Execute(null);pump(100);check("详情页可切换到版本安装",download.ShowsResourceVersions&&Find<ScrollViewer>("ResourceReleaseScroll").IsEffectivelyVisible);capture("pack-versions-dark",true);
        download.BackToResourcesCommand.Execute(null);model.SelectedTheme=model.ThemeChoices.Single(t=>t.Id=="glacier");
    }
}
