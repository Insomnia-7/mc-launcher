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
using Launcher.Infrastructure.Processes;
using Microsoft.Extensions.DependencyInjection;

// Explicit opt-in network/device acceptance. Uses production installers, Java discovery,
// durable tasks and WinProcessLauncher; only observes the process adapter's output.
internal static class RealGameChecks
{
    [DllImport("user32.dll")] private static extern bool PostMessage(nint hwnd,uint message,nint wParam,nint lParam);
    public static int Start(string root)
    {
        Environment.SetEnvironmentVariable("MCLAUNCHER_HOME",Path.Combine(root,"data"));
        Environment.SetEnvironmentVariable("MCLAUNCHER_GAME_ROOT",Path.Combine(root,"game"));
        Directory.CreateDirectory(Path.Combine(root,"game"));
        AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();AppPaths.EnsureCreated();
        var observer=new ObservedLauncher();using var services=LauncherServices.Create(s=>s.AddSingleton<IProcessLauncher>(observer));
        var model=services.GetRequiredService<MainViewModel>();using var stop=new CancellationTokenSource();
        var window=new MainWindow{DataContext=model,Width=1180,Height=800};PlatformUi.Window=window;var exit=1;var passed=0;
        void Check(string name,bool okay){if(!okay)throw new InvalidOperationException(name+"; "+model.SetupError+"; "+model.StatusText);passed++;Console.WriteLine("PASS "+name);}
        window.Show();Dispatcher.UIThread.Post(async()=>
        {
            try
            {
                await model.OpenSetupCommand.ExecuteAsync(null);
                model.SetupVersion=model.SetupVersions.FirstOrDefault(v=>v.Id=="1.20.1");
                Check("生产版本列表可选择Minecraft1.20.1",model.SetupVersion is not null);
                model.SetupOfflineName="FangyuM9";model.SetupMemoryText="2048";model.SetupLaunchAfterInstall=false;
                Check("隔离游戏目录已确认且可写",!model.HasGameRootProblem);
                var cancelled=model.InstallSetupCommand.ExecuteAsync(null);await Task.Yield();
                Check("真实安装任务已开始",model.IsSetupBusy);
                model.CancelCommand.Execute(null);await cancelled;
                Check("真实安装可取消并恢复向导交互",model.StatusText=="已取消" && !model.IsSetupBusy && model.InstallSetupCommand.CanExecute(null));
                Console.WriteLine("Installing real game in isolated D: directory...");
                await model.InstallSetupCommand.ExecuteAsync(null);
                Check("真实游戏安装完成且出现在实例列表",model.Instances.Any(i=>i.VersionId=="1.20.1") && string.IsNullOrEmpty(model.SetupError));
                model.SelectedInstance=model.Instances.Single(i=>i.VersionId=="1.20.1");
                model.ShowHomeCommand.Execute(null);await Task.Delay(200);
                await model.LaunchCommand.ExecuteAsync(null);
                Check("一键启动交给真实Windows进程适配器",observer.Process is not null && observer.Plan?.MainClass=="net.minecraft.client.main.Main");
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
                    if(process.MainWindowHandle!=0 && title.Contains("Minecraft") && text.Contains("OpenAL initialized") && text.Contains("Created:") && text.Contains("textures/atlas")){ready=true;break;}
                }
                Check("真实客户端创建游戏窗口并完成音频与纹理初始化",ready);
                Check("游戏启动后主界面恢复可交互",!model.IsBusy && window.IsVisible);
                PostMessage(process.MainWindowHandle,0x10,0,0);
                for(var i=0;i<30 && !process.HasExited;i++){await Task.Delay(200);process.Refresh();}
                var exitCode=await observer.Exit!.WaitAsync(TimeSpan.FromSeconds(10));
                Check("真实游戏窗口可正常退出",process.HasExited && exitCode==0);
                File.WriteAllText(Path.Combine(root,"real-game-acceptance.json"),JsonSerializer.Serialize(new{passed,version="1.20.1",identity="isolated offline test identity",actualProcess=true,windowTitle=title,
                    graphicsInitialized=true,audioInitialized=true,normalExit=true,exitCode,scope="Production install cancel/retry, instance selection, launch and normal window close. No Microsoft account, world play, modded launch or remote room tested."},new JsonSerializerOptions{WriteIndented=true}));
                Console.WriteLine($"{passed} real game checks passed");exit=0;
            }
            catch(Exception ex){Console.Error.WriteLine(ex);}
            finally
            {
                // Only the process created by this isolated acceptance run may be stopped.
                if(observer.Process is {HasExited:false} own)own.Kill();
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
