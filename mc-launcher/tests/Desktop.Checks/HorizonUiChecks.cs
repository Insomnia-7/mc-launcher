using Avalonia.Controls;
using Avalonia.VisualTree;
using Launcher.App;
using Launcher.App.ViewModels;
using Launcher.Desktop;
using Launcher.Desktop.Controls;
using Launcher.Desktop.Views;
using Launcher.Infrastructure.Horizon;

internal static class HorizonUiChecks
{
    public static void Run(MainViewModel model,MainWindow window,string root,Action<int> pump,Action<string,bool> check,Action<string,bool> capture)
    {
        void Complete(Task task){var watch=System.Diagnostics.Stopwatch.StartNew();while(!task.IsCompleted&&watch.ElapsedMilliseconds<30000)pump(20);task.GetAwaiter().GetResult();pump(100);}
        T Find<T>(string name)where T:Control=>window.GetVisualDescendants().OfType<T>().Single(x=>x.Name==name);
        var java=model.SelectedInstance;var javaCount=model.Instances.Count;
        model.OpenHorizonCommand.Execute(null);pump(120);
        check("Rust 页面归属实例主导航",model.ShellPage=="horizon"&&model.IsLibraryPage&&Find<Button>("LibraryNav").Classes.Contains("active"));
        check("Rust 页面单独挂载",Find<AnimatedContentHost>("PageHost").Content is HorizonView);
        check("Rust 页面保留实例导航高亮背景",Find<Border>("NavSelection").IsVisible&&Find<Border>("NavIndicator").IsVisible);
        model.CreateHorizonInstanceCommand.Execute(null);
        check("未选择构建不能创建实例",model.HorizonInstances.Count==0&&model.HorizonHint.Contains("构建"));
        var directory=HorizonFixture.CreateBuild(Path.Combine(root,"native-fixture"));Complete(model.ImportHorizonBuildAsync(directory));
        check("导入完整构建会检测接口",model.HorizonBuilds.Count==1&&model.SelectedHorizonBuild?.Minecraft=="26.3");
        model.HorizonName=" ";model.CreateHorizonInstanceCommand.Execute(null);check("空白名称不会保存",model.HorizonInstances.Count==0);
        model.HorizonName="Rust 生存世界";model.CreateHorizonInstanceCommand.Execute(null);model.CreateHorizonInstanceCommand.Execute(null);
        check("同名 Rust 实例保持不同标识",model.HorizonInstances.Count==2&&model.HorizonInstances[0].Id!=model.HorizonInstances[1].Id);
        check("Rust 实例不混入 Java 启动列表",model.Instances.Count==javaCount&&ReferenceEquals(java,model.SelectedInstance));
        check("总计包含两类实例",model.InstanceCountText==$"{javaCount+2} 个游戏实例");
        Complete(model.PrepareHorizonCommand.ExecuteAsync(null));
        check("离线身份不能下载 Rust 游戏资源",model.HorizonHint.Contains("微软账号")&&!Directory.Exists(Path.Combine(AppPaths.DataDirectory,"horizon","minecraft")));
        Complete(model.LaunchHorizonCommand.ExecuteAsync(null));
        check("离线身份不能启动 Rust 客户端",model.HorizonHint.Contains("微软账号")&&!File.Exists(Path.Combine(AppPaths.DataDirectory,"horizon","instances",model.SelectedHorizonInstance!.Id,"argv.json")));
        check("账号检查失败后恢复操作",!model.HorizonBusy&&model.CanEditHorizon);
        using(var http=new HttpClient())
        {
            var reloaded=new HorizonClient(http,AppPaths.DataDirectory);
            check("Rust 实例独立持久化且无会话凭据",File.ReadAllText(Path.Combine(reloaded.Root,"instances.json")).Contains("Rust")&&!File.ReadAllText(Path.Combine(reloaded.Root,"instances.json")).Contains("access_token"));
        }
        foreach(var theme in new[]{"light","dark"})
        {
            model.SelectThemeModeCommand.Execute(theme);pump(100);capture("horizon-"+theme,true);
            window.Width=1000;window.Height=680;pump(100);
            check($"{theme} 最小窗口保留底部状态",Find<TextBlock>("HorizonStatus").IsEffectivelyVisible&&Find<TextBlock>("HorizonStatus").Bounds.Height>0);
            capture("horizon-compact-"+theme,true);window.Width=1180;window.Height=800;pump(60);
        }
        model.OpenNetworkCommand.Execute(null);pump(80);check("Rust 到网络只选中网络",model.ShellPage=="network"&&!model.IsLibraryPage);
        model.OpenAppearanceCommand.Execute(null);pump(80);check("Rust 到设置只选中设置",model.IsSettingsPage&&!model.IsLibraryPage);
        model.ShowInstancesCommand.Execute(null);pump(100);
        check("返回 Java 实例保留原选择",model.ShellPage=="library"&&ReferenceEquals(java,model.SelectedInstance));
    }
}
