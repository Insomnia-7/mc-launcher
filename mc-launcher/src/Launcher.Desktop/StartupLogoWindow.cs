using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Launcher.Desktop;

internal sealed class StartupLogoWindow : Window
{
    internal TextBlock BrandText {get;}
    internal StackPanel Mark {get;}
    private readonly Bitmap _icon;
    public StartupLogoWindow()
    {
        Width=240;Height=180;CanResize=false;ShowInTaskbar=false;
        WindowDecorations=WindowDecorations.None;WindowStartupLocation=WindowStartupLocation.CenterScreen;
        TransparencyLevelHint=[WindowTransparencyLevel.Transparent];
        Background=Brushes.Transparent;TransparencyBackgroundFallback=Brushes.Transparent;
        _icon=new Bitmap(AssetLoader.Open(new Uri("avares://MCLauncher.Desktop/Assets/Brand/fangyu.png")));
        BrandText=new TextBlock{Name="StartupBrandText",Text="方隅",FontSize=25,Foreground=Brushes.White,HorizontalAlignment=HorizontalAlignment.Center};
        // LCD subpixel text assumes an opaque background and produces dark/color fringes on alpha.
        TextOptions.SetTextRenderingMode(BrandText,TextRenderingMode.Antialias);
        Mark=new StackPanel{Spacing=14,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
        Mark.Children.Add(new Image{Source=_icon,Width=76,Height=76});Mark.Children.Add(BrandText);Content=Mark;
        Closed+=(_,_)=>_icon.Dispose();
    }

    internal static bool BeforeMainWindow(IClassicDesktopStyleApplicationLifetime desktop,MainWindow main)
    {
        if(!OperatingSystem.IsWindows())return false;
        var splash=new StartupLogoWindow();desktop.MainWindow=splash;
        var loading=new CancellationTokenSource();
        var closed=false;var openingMain=false;
        splash.Closed+=(_,_)=>{closed=true;loading.Cancel();if(!openingMain)main.Close();};
        splash.Opened+=(_,_)=>Dispatcher.UIThread.Post(async ()=>
        {
            try
            {
                await Task.WhenAll(main.PrepareStartupContentAsync(loading.Token),
                    Task.Delay(MotionPreferences.Enabled ? MotionTokens.StartupLogoHold : TimeSpan.Zero,loading.Token));
                if(closed)return;
                if(MotionPreferences.Enabled&&splash.ActualTransparencyLevel==WindowTransparencyLevel.Transparent)
                    await Motion.AnimateOpacity(splash.Mark,1,0,MotionTokens.StartupLogoExit,new Avalonia.Animation.Easings.CubicEaseOut(),loading.Token);
                if(closed)return;
                openingMain=true;
                main.PrepareStartupEntrance();desktop.MainWindow=main;main.Show();splash.Close();
            }
            catch(OperationCanceledException)when(loading.IsCancellationRequested){}
            finally{loading.Dispose();}
        },DispatcherPriority.Loaded);
        return true;
    }
}
