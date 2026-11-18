using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Threading;
using Launcher.App;
using Launcher.App.ViewModels;
using Launcher.Application.Abstractions;
using Launcher.Desktop;
using Launcher.Domain.Launch;
using Launcher.Domain.Content;
using Launcher.Infrastructure.Processes;
using Microsoft.Extensions.DependencyInjection;

// Explicit opt-in network/device acceptance. Uses production installers, Java discovery,
// durable tasks and WinProcessLauncher; only observes the process adapter's output.
internal static class RealGameChecks
{
    [DllImport("user32.dll")] private static extern bool PostMessage(nint hwnd,uint message,nint wParam,nint lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    public static int Start(string root)
    {
        Environment.SetEnvironmentVariable("MCLAUNCHER_HOME",Path.Combine(root,"data"));
        Environment.SetEnvironmentVariable("MCLAUNCHER_GAME_ROOT",Path.Combine(root,"game"));
        Directory.CreateDirectory(Path.Combine(root,"game"));
        AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();AppPaths.EnsureCreated();
        var observer=new ObservedLauncher();using var services=LauncherServices.Create(s=>s.AddSingleton<IProcessLauncher>(observer));
        var model=services.GetRequiredService<MainViewModel>();using var stop=new CancellationTokenSource();
        var window=new MainWindow{DataContext=model,Width=1180,Height=800};PlatformUi.Window=window;var exit=1;var passed=0;
        void Check(string name,bool okay){if(!okay)throw new InvalidOperationException(name+"; "+model.Download.Status+"; "+model.StatusText+"; "+model.DiagnosisText+"; "+JsonSerializer.Serialize(model.InspectionIssues));passed++;Console.WriteLine("PASS "+name);}
        window.Show();Dispatcher.UIThread.Post(async()=>
        {
            try
            {
                model.CreateInstanceCommand.Execute(null);
                model.OfflineName="FangyuAcceptance";
                var version=Environment.GetEnvironmentVariable("FANGYU_REAL_GAME_VERSION")??"26.3";
                var project=Environment.GetEnvironmentVariable("FANGYU_REAL_MODPACK_PROJECT");
                if(project is null)
                {
                    model.Download.SelectVersionCommand.Execute(new VersionItem(version,"release",DateTimeOffset.UtcNow));
                    Check("发现资源可配置指定游戏版本",model.Download.IsVersionConfiguration&&model.Download.SelectedVersion?.Id==version);
                    Check("隔离游戏目录已确认且可写",!model.HasGameRootProblem);
                    Console.WriteLine("Installing real game in isolated D: directory...");
                    await model.Download.InstallVersionCommand.ExecuteAsync(null);
                    Check("真实游戏安装完成且出现在实例列表",model.Instances.Any(i=>i.VersionId==version));
                    model.ShowHomeCommand.Execute(null);await Task.Delay(200);
                    model.SelectedInstance=model.Instances.Single(i=>i.VersionId==version);
                    await model.LaunchCommand.ExecuteAsync(null);
                }
                else
                {
                    var vm=model.Download;vm.SelectedCategory=vm.Categories.First(c=>c.Category==DownloadCategory.Modpack);
                    var packTitle=Environment.GetEnvironmentVariable("FANGYU_REAL_MODPACK_TITLE")??"Fresh & Smooth";
                    vm.SelectedSource=DownloadViewModel.ModrinthSource;vm.Query=packTitle;
                    vm.GameVersionFilter=version;vm.LoaderFilter="fabric";
                    await vm.SearchCommand.ExecuteAsync(null);var item=vm.Results.Single(r=>r.Id==project);
                    Check("官方搜索找到指定整合包",item.Title==packTitle);
                    await vm.OpenResourceCommand.ExecuteAsync(item);vm.ShowPackVersionsCommand.Execute(null);
                    vm.ResourceReleaseChannel="全部版本";
                    if(vm.ResourceReleaseGroups.Count==0)throw new InvalidOperationException("没有读取到目标整合包发布版本："+vm.ResourceVersionsStatus);
                    var release=vm.ResourceReleaseGroups.SelectMany(g=>g.Releases).First(r=>r.GameVersions.Contains(version)&&r.Loaders.Contains("fabric"));
                    vm.SelectResourceReleaseCommand.Execute(release);
                    Console.WriteLine("Installing official pack release: "+release.Name);
                    if(!vm.ResourceIsInstalled)await vm.ResourcePrimaryCommand.ExecuteAsync(null);
                    Check("真实整合包安装后按钮变为启动游戏",vm.ResourceIsInstalled&&vm.ResourceInstallButtonText=="启动游戏");
                    await vm.ResourcePrimaryCommand.ExecuteAsync(null);
                    Check("启动按钮使用实际整合包实例",model.SelectedInstance?.IsModpack==true);
                }
                Check("一键启动交给真实Windows进程适配器",observer.Process is not null&&observer.Plan?.MainClass==(project is null?"net.minecraft.client.main.Main":"net.fabricmc.loader.impl.launch.knot.KnotClient"));
                using var process=Process.GetProcessById(observer.Process!.Id);
                var ready=false;var title="";var text="";
                for(var i=0;i<180;i++)
                {
                    await Task.Delay(500);process.Refresh();if(process.HasExited)break;
                    title=process.MainWindowTitle;
                    try
                    {
                        using var log=new FileStream(observer.Log!,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
                        using var reader=new StreamReader(log);text=await reader.ReadToEndAsync();
                    }
                    catch(IOException){}
                    if(process.MainWindowHandle!=0 && IsWindowVisible(process.MainWindowHandle)&&title.Contains("Minecraft") && text.Contains("OpenAL initialized") && text.Contains("Created:") && text.Contains("textures/atlas")){ready=true;break;}
                }
                Check("真实客户端创建游戏窗口并完成音频与纹理初始化",ready);
                Check("游戏启动后主界面恢复可交互",!model.IsBusy && window.IsVisible);
                PostMessage(process.MainWindowHandle,0x10,0,0);
                for(var i=0;i<30 && !process.HasExited;i++){await Task.Delay(200);process.Refresh();}
                var exitCode=await observer.Exit!.WaitAsync(TimeSpan.FromSeconds(10));
                Check("真实游戏窗口可正常退出",process.HasExited && exitCode==0);
                File.WriteAllText(Path.Combine(root,project is null?"real-game-acceptance.json":"real-modpack-acceptance.json"),JsonSerializer.Serialize(new{passed,version,project,identity="isolated offline test identity",actualProcess=true,windowTitle=title,
                    graphicsInitialized=true,audioInitialized=true,normalExit=true,exitCode,scope="Production resource installation, instance selection, visible game window, launch and normal window close. No Microsoft account, world play or remote room tested."},new JsonSerializerOptions{WriteIndented=true}));
                Console.WriteLine($"{passed} real game checks passed");exit=0;
            }
            catch(Exception ex){Console.Error.WriteLine(ex);}
            finally
            {
                // Only the process created by this isolated acceptance run may be stopped.
                if(observer.Process is {} own)try{if(!own.HasExited)own.Kill();}catch(InvalidOperationException){}
                window.Close();model.FlushAppearance();model.Multiplayer?.Dispose();PlatformUi.Window=null;stop.Cancel();
            }
        });Dispatcher.UIThread.MainLoop(stop.Token);return exit;
    }
    private sealed class ObservedLauncher:IProcessLauncher
    {
        public IGameProcess? Process;public LaunchPlan? Plan;public string? Log;public Task<int>? Exit;
        public IGameProcess Start(LaunchPlan plan,string log,IReadOnlyDictionary<string,string>? environment=null)
        {Plan=plan;Log=log;Process=new WinProcessLauncher().Start(plan,log,environment);Exit=Process.WaitForExitAsync();return Process;}
    }
}
