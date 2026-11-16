using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Launcher.App;
using Launcher.App.ViewModels;
using Launcher.Desktop;
using Microsoft.Extensions.DependencyInjection;

internal static class StartupChecks
{
    public static int Start(string root)
    {
        Environment.SetEnvironmentVariable("MCLAUNCHER_HOME",Path.Combine(root,"data"));
        Environment.SetEnvironmentVariable("MCLAUNCHER_GAME_ROOT",Path.Combine(root,"game"));
        AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();AppPaths.EnsureCreated();
        using var services=LauncherServices.Create();var model=services.GetRequiredService<MainViewModel>();
        model.SelectedMotion=model.MotionChoices.Single(x=>x.Id=="smooth");
        using var stop=new CancellationTokenSource();var passed=0;var exit=1;
        void Check(string name,bool ok){if(!ok)throw new InvalidOperationException(name);passed++;Console.WriteLine("PASS "+name);}
        var splash=new StartupLogoWindow();splash.Show();
        Dispatcher.UIThread.Post(async ()=>
        {
            MainWindow? main=null;
            try
            {
                await Task.Delay(200);
                Check("启动标识使用原生透明窗口",splash.ActualTransparencyLevel==WindowTransparencyLevel.Transparent&&splash.Background==Brushes.Transparent&&splash.TransparencyBackgroundFallback==Brushes.Transparent);
                Check("透明启动标识没有边框与任务栏项目",splash.WindowDecorations==WindowDecorations.None&&!splash.ShowInTaskbar);
                Check("启动文字明确使用灰度抗锯齿且没有描边效果",TextOptions.GetTextRenderingMode(splash.BrandText)==TextRenderingMode.Antialias&&splash.BrandText.Effect is null);
                using(var image=new RenderTargetBitmap(new PixelSize(160,48),new Vector(96,96)))
                {
                    image.Render(splash.BrandText);image.Save(Path.Combine(root,"startup-text-alpha.png"),PngBitmapEncoderOptions.Default);
                    var bytes=new byte[160*48*4];var memory=Marshal.AllocHGlobal(bytes.Length);
                    try{image.CopyPixels(new PixelRect(0,0,160,48),memory,bytes.Length,160*4);Marshal.Copy(memory,bytes,0,bytes.Length);}finally{Marshal.FreeHGlobal(memory);}
                    var visible=0;var edges=0;var bad=0;
                    for(var i=0;i<bytes.Length;i+=4)
                    {
                        var alpha=bytes[i+3];if(alpha==0)continue;visible++;if(alpha<255)edges++;
                        // RenderTargetBitmap uses premultiplied BGRA: plain white has RGB == alpha.
                        if(Math.Abs(bytes[i]-alpha)>2||Math.Abs(bytes[i+1]-alpha)>2||Math.Abs(bytes[i+2]-alpha)>2)bad++;
                    }
                    Check("启动文字存在平滑半透明边缘",visible>100&&edges>20);
                    Check("启动文字所有可见像素均为纯白且无黑色彩色边",bad==0);
                    File.WriteAllText(Path.Combine(root,"startup-text-audit.json"),JsonSerializer.Serialize(new{visible,edges,bad},new JsonSerializerOptions{WriteIndented=true}));
                }
                using(var image=new RenderTargetBitmap(new PixelSize(240,180),new Vector(96,96))){image.Render(splash);image.Save(Path.Combine(root,"startup-logo-transparent.png"),PngBitmapEncoderOptions.Default);}
                main=new MainWindow{DataContext=model,Width=1280,Height=820};main.PrepareStartupEntrance();
                var navNames=new[]{"HomeNav","LibraryNav","MultiplayerNav","NetworkNav","DiscoverNav","TaskCenterEntry","SettingsEntry"};
                var nav=navNames.Select(name=>main.FindControl<Button>(name)!).ToArray();
                Check("主窗口展示前已准备七个导航入场",nav.Length==7&&nav.All(x=>x.Opacity==0));
                main.Show();splash.Close();await Task.Delay(350);
                Check("主界面背景仍为原生实色",main.ActualTransparencyLevel==WindowTransparencyLevel.None&&main.Background is ISolidColorBrush{Color.A:255});
                Check("导航项目由上至下错开进入",nav[0].Opacity>nav[4].Opacity&&nav[4].Opacity>=nav[6].Opacity);
                await main.StartupEntranceCompletion;await Task.Delay(50);
                Check("入场结束后七个导航完全显示可点击",nav.All(x=>x.Opacity==1&&x.IsHitTestVisible));
                Check("所有入场位移均已释放",!main.IsStartupEntranceActive&&nav.All(x=>(x.RenderTransform?.Value.M31??0)==0));
                using(var image=new RenderTargetBitmap(new PixelSize(1280,820),new Vector(96,96))){image.Render(main);image.Save(Path.Combine(root,"startup-main-complete.png"),PngBitmapEncoderOptions.Default);}
                main.Close();
                foreach(var reason in new[]{"关闭动画","改变尺寸","最小化","切换页面"})
                {
                    model.ShowHomeCommand.Execute(null);model.SelectedMotion=model.MotionChoices.Single(x=>x.Id=="smooth");
                    main=new MainWindow{DataContext=model,Width=1280,Height=820};main.PrepareStartupEntrance();main.Show();await Task.Delay(150);
                    if(reason=="关闭动画")model.SelectedMotion=model.MotionChoices.Single(x=>x.Id=="off");
                    else if(reason=="改变尺寸")main.Width=1100;
                    else if(reason=="最小化")main.WindowState=WindowState.Minimized;
                    else model.ShowInstancesCommand.Execute(null);
                    await Task.Delay(150);
                    Check(reason+"时取消入场并恢复可见",!main.IsStartupEntranceActive&&navNames.Select(name=>main.FindControl<Button>(name)!).All(x=>x.Opacity==1&&x.IsHitTestVisible));main.Close();
                }
                model.SelectedMotion=model.MotionChoices.Single(x=>x.Id=="off");
                main=new MainWindow{DataContext=model};main.PrepareStartupEntrance();Check("关闭动画时跳过入场准备",!main.IsStartupEntranceActive);main.Close();
                File.WriteAllText(Path.Combine(root,"startup-native.json"),JsonSerializer.Serialize(new{passed,scope="Current Windows native transparency, grayscale glyph alpha, staggered entry, opaque main window, interruption handling"},new JsonSerializerOptions{WriteIndented=true}));
                Console.WriteLine($"{passed} native startup checks passed");exit=0;
            }
            catch(Exception e){Console.Error.WriteLine(e);}
            finally{main?.Close();splash.Close();stop.Cancel();}
        });
        Dispatcher.UIThread.MainLoop(stop.Token);return exit;
    }
}
