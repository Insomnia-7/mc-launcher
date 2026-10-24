using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Logging;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Launcher.App;
using Launcher.App.ViewModels;
using Launcher.Application.Abstractions;
using Launcher.Application.Install;
using Launcher.Desktop;
using Launcher.Desktop.Controls;
using Launcher.Domain.Java;
using Launcher.Domain.Launch;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var root = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--")) ?? Path.Combine(Path.GetTempPath(), "mclauncher-desktop-checks-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var data = Path.Combine(root, "fixture"); var game = Path.Combine(data, "game");
        Environment.SetEnvironmentVariable("MCLAUNCHER_HOME", data);
        Environment.SetEnvironmentVariable("MCLAUNCHER_GAME_ROOT", game);
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "appearance.json"), args.Contains("--legacy-theme")
            ? """{"themeId":"custom","customTheme":{"schema":1,"name":"旧彩色主题","colors":{"BgBrush":"#FF0000"}},"wallpaperOpacity":0.35,"motionMode":"off"}"""
            : """{"themeId":"glacier","motionMode":"off"}""");
        foreach (var id in args.Contains("--empty") ? [] : new[] { "1.21", "1.21.4", "26.3" })
        {
            var directory = Path.Combine(game, "versions", id); Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, id + ".json"), JsonSerializer.Serialize(new { id, type = "release", mainClass = "net.minecraft.client.main.Main", arguments = new { jvm = new[] { "-cp", "${classpath}" }, game = new[] { "--username", "${auth_player_name}" } }, libraries = Array.Empty<object>(), javaVersion = new { majorVersion = 21 } }));
            File.WriteAllBytes(Path.Combine(directory, id + ".jar"), [80,75,3,4]);
        }
        var javaPath = Path.Combine(data, OperatingSystem.IsWindows() ? "java.exe" : "java"); File.WriteAllBytes(javaPath, [0]);
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        var log = new BindingLog(); Logger.Sink = log;
        var http = new OfflineHandler(); var launch = new TestLauncher();
        using var services = LauncherServices.Create(s => { s.AddSingleton(new HttpClient(http)); s.AddSingleton<IJavaLocator>(new TestJava(javaPath)); s.AddSingleton<IProcessLauncher>(launch); });
        var clock = Stopwatch.StartNew();
        var model = services.GetRequiredService<MainViewModel>();
        var window = new MainWindow { DataContext = model, Width = 1180, Height = 800 }; PlatformUi.Window = window;
        window.Show(); Pump(120);
        var readyMs = clock.Elapsed.TotalMilliseconds;
        var passed = 0;
        void Check(string name, bool condition) { if (!condition) throw new InvalidOperationException(name); passed++; Console.WriteLine("PASS " + name); }
        T Find<T>(string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
        void Capture(string name)
        {
            window.MouseMove(new Point(0,0)); window.MouseMove(new Point(0,0)); Pump(80); window.UpdateLayout();
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height), new Vector(96,96));
            bitmap.Render(window); bitmap.Save(Path.Combine(root, name + ".png"));
        }
        void Click(Control control)
        {
            var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window) ?? throw new InvalidOperationException("Control is detached");
            window.MouseMove(point); window.MouseDown(point, MouseButton.Left); Pump(25); window.MouseUp(point, MouseButton.Left); Pump(120);
        }
        try
        {
            if(args.Contains("--legacy-theme"))
            {
                Check("旧彩色主题迁移为夜间黑", !model.IsLightTheme && model.SelectedTheme?.Id=="aurora" && model.ThemeChoices.Count==2);
                var saved=Launcher.Application.Settings.AppearanceStore.Read(data);
                Check("迁移保存黑白选项并清除旧配色", saved.ThemeId=="aurora" && saved.CustomTheme is null);
                Check("迁移保留其他外观偏好", saved.MotionMode=="off" && saved.WallpaperOpacity==0.35);
                Check("旧主题迁移后无绑定错误", log.Errors.Count==0);
                return 0;
            }
            if(args.Contains("--empty"))
            {
                Check("首次启动显示安装向导", model.IsSetupOpen && model.Instances.Count==0);
                Check("空游戏库也不阻塞首页联网", http.Requests==0);
                Capture("first-run");
                model.CloseSetupCommand.Execute(null); Pump(50); Capture("empty-library");
                Check("可以跳过向导后继续使用", !model.IsSetupOpen && !model.HasInstances);
                Check("首次启动页面没有绑定错误", log.Errors.Count==0);
                return 0;
            }
            Check("真实版本目录加载", model.Instances.Count == 3 && model.SelectedInstance is not null);
            Check("仅提供日间白和夜间黑", model.ThemeChoices.Count==2 && model.ThemeChoices.Select(t=>t.Name).SequenceEqual(new[]{"日间 · 白","夜间 · 黑"}));
            Check("全部主题色均为灰阶", model.ThemeChoices.SelectMany(t=>t.Definition.Colors.Values).Select(Color.Parse).All(c=>c.R==c.G && c.G==c.B));
            Check("首页构造不联网", http.Requests == 0);
            Check("新版界面不依赖 WPF 程序集", !typeof(MainWindow).Assembly.GetReferencedAssemblies().Any(a=>a.Name is "PresentationCore" or "PresentationFramework" or "WindowsBase"));
            Check("缺少安全存储时不降级明文令牌", !PlatformUi.CanStoreSecrets(null));
            Check("首页启动按钮连接共用业务命令", Find<Button>("LaunchButton").Command == model.LaunchCommand);
            Check("主操作接入矢量图标", Find<Button>("LaunchButton").Content is IconLabel { Icon:"ActionPlay" } && Find<Button>("LibraryNav").Content is IconLabel { Icon:"NavVersions" });
            Check("图标保留素材原色", IconCatalog.Items["Tile.LoaderVanilla"].Parts.Where(p=>p.Fill is not null).Select(p=>Color.Parse(p.Fill!)).Any(c=>c.R!=c.G || c.G!=c.B));
            Check("全部矢量图标可解码", IconCatalog.Items.Values.SelectMany(icon=>icon.Parts).All(part=>Geometry.Parse(part.Path) is not null));
            Check("版本类型图标可区分加载器与整合包", new InstanceIconConverter().Convert(new InstanceCard("fabric","fabric-loader-0.16.9-1.21","Fabric","Fabric","1.21",false),typeof(string),null,System.Globalization.CultureInfo.InvariantCulture).ToString()=="Tile.LoaderFabric" && new InstanceIconConverter().Convert(new InstanceCard("pack","1.21","Pack","Pack","1.21",true),typeof(string),null,System.Globalization.CultureInfo.InvariantCulture).ToString()=="Tile.LoaderModpack");
            Capture("home-light");
            Click(Find<Button>("AccountEntry")); Check("账号入口可点击", model.IsAccountsOpen);
            Capture("accounts"); model.CloseAccountsCommand.Execute(null); Pump(50);
            model.OpenAppearanceCommand.Execute(null); Pump(50); Check("主题面板可打开", Find<ListBox>("ThemeList").IsEffectivelyVisible);
            model.SelectedTheme = model.ThemeChoices.Single(t => t.Id == "aurora"); Pump(50);
            Check("主题切换与保存", !model.IsLightTheme && File.ReadAllText(Path.Combine(data,"appearance.json")).Contains("aurora"));
            Capture("appearance"); model.CloseAppearanceCommand.Execute(null); Pump(50); Capture("home-dark");
            Click(Find<ToggleButton>("ThemeToggle"));
            Check("昼夜按钮可切回白天并保存", model.IsLightTheme && Launcher.Application.Settings.AppearanceStore.Read(data).ThemeId=="glacier");
            var primaryForeground=(ISolidColorBrush)Avalonia.Application.Current!.Resources["PrimaryTextBrush"]!;
            Check("日间主按钮使用白字", primaryForeground.Color==Colors.White);
            Click(Find<ToggleButton>("ThemeToggle"));
            primaryForeground=(ISolidColorBrush)Avalonia.Application.Current.Resources["PrimaryTextBrush"]!;
            Check("夜间主按钮切为黑字", !model.IsLightTheme && primaryForeground.Color==Colors.Black);
            Check("夜间矢量图标同步主按钮字色", Find<Button>("LaunchButton").GetVisualDescendants().OfType<UiIcon>().Single().Foreground is ISolidColorBrush { Color:var iconColor } && iconColor==Colors.Black);
            Check("其他配色入口已移除", !window.GetVisualDescendants().OfType<Button>().Any(b=>b.Content is string name && name is "导入配色" or "导出配色"));
            model.IsSettingsOpen = true; Pump(40);
            model.MemoryMb = 3072; model.SaveSettingsCommand.Execute(null);
            Check("版本设置写入真实实例", InstanceSettings.Load(game,model.SelectedInstance!.InstanceId).MemoryMb == 3072);
            model.IsSettingsOpen = false;
            model.SelectedInstance = model.Instances[1]; Pump(50);
            Check("版本选择更新首页", Find<TextBlock>("WorldVersion").Text == model.SelectedInstance.BaseVersion);
            var before = model.SelectedInstance.InstanceId;
            Click(Find<Button>("LaunchButton")); PumpUntil(() => !model.IsBusy, 3000);
            Check("启动按钮走真实启动准备并交给进程适配器", launch.Starts == 1 && launch.Plan?.MainClass == "net.minecraft.client.main.Main");
            Check("启动后恢复可交互", !model.IsBusy);
            // Switch the visual section directly: a navigation rendering check must not fetch public version lists.
            for (var i=0;i<20;i++) model.IsInstanceSection = i%2==0;
            model.IsInstanceSection=false; Pump(80); Capture("discover");
            Check("资源页可渲染", window.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="发现资源"));
            model.IsInstanceSection=true; model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="smooth"); Pump(300);
            for(var i=0;i<8;i++) { model.IsInstanceSection=!model.IsInstanceSection; Pump(12); }
            model.IsInstanceSection=true; Pump(350);
            Check("开启动画时快速往返切页仍显示当前页面", Find<Button>("LaunchButton").IsEffectivelyVisible && Find<Button>("LaunchButton").IsEffectivelyEnabled);
            model.OpenAppearanceCommand.Execute(null); model.CloseAppearanceCommand.Execute(null); model.OpenAccountsCommand.Execute(null); Pump(250);
            Check("快速切换弹窗后可继续操作", model.IsAccountsOpen && !model.IsAppearanceOpen);
            model.CloseAccountsCommand.Execute(null); Pump(250);
            var feedback=Find<Button>("AccountEntry"); var point=feedback.TranslatePoint(new Point(feedback.Bounds.Width/2,feedback.Bounds.Height/2),window)!.Value;
            window.MouseMove(point); window.MouseDown(point,MouseButton.Left); Pump(140);
            Check("按下具有缩放反馈", feedback.IsPressed && feedback.RenderTransform!.Value.M11<1);
            window.MouseUp(point,MouseButton.Left); Pump(350); model.CloseAccountsCommand.Execute(null); Pump(180);
            Check("松开后恢复正常尺寸", feedback.RenderTransform!.Value.M11>=0.99);
            Find<Button>("LibraryNav").Focus(NavigationMethod.Tab); feedback.Focus(NavigationMethod.Tab); Pump(80);
            Check("键盘导航显示焦点框", feedback.IsFocused && feedback.GetVisualDescendants().OfType<Border>().Single(b=>b.Name=="FocusRing").IsVisible);
            feedback.IsEnabled=false; Pump(30); Click(feedback);
            Check("禁用按钮不可触发账号弹窗", !model.IsAccountsOpen && !feedback.IsEffectivelyEnabled && feedback.Opacity<0.6);
            feedback.IsEnabled=true;
            model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="off"); Check("关闭动画立即生效", !MotionPreferences.Enabled && window.Classes.Contains("reduced-motion"));
            window.Width=900; window.Height=640; Pump(60); Capture("home-compact");
            Check("最小窗口下启动入口仍在窗口内", Find<Button>("LaunchButton").TranslatePoint(new Point(0,0),window) is { X:>=0,Y:>=0 } p && p.X+Find<Button>("LaunchButton").Bounds.Width<=window.ClientSize.Width+1);
            Check("无数据绑定错误", log.Errors.Count==0);
            using(var portableServices=LauncherServices.Create(s=>s.RemoveAll<ISecretProtector>()))
            {
                var portableModel=portableServices.GetRequiredService<MainViewModel>();
                Check("缺少 Windows 加密服务时仍能创建跨平台首页", portableModel.Instances.Count==3);
                Check("缺少安全存储时禁用微软登录入口", !portableModel.LoginCommand.CanExecute(null));
            }
            File.WriteAllText(Path.Combine(root,"checks.json"),JsonSerializer.Serialize(new { passed, viewConstructionAndFirstLayoutMs=readyMs, networkRequestsBeforeUserActions=0, launchRequests=launch.Starts, bindingErrors=log.Errors },new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine($"{passed} checks passed. View construction and initial layout: {readyMs:F0} ms");
            return 0;
        }
        catch(Exception e)
        {
            Console.Error.WriteLine(e); foreach(var error in log.Errors) Console.Error.WriteLine(error);
            Capture("failure"); return 1;
        }
        finally { window.Close(); PlatformUi.Window=null; }
    }
    private static void Pump(int milliseconds)
    {
        var clock=Stopwatch.StartNew();
        do { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); } while(clock.ElapsedMilliseconds<milliseconds);
        Dispatcher.UIThread.RunJobs();
    }
    private static void PumpUntil(Func<bool> condition,int timeout) { var clock=Stopwatch.StartNew(); while(!condition() && clock.ElapsedMilliseconds<timeout) Pump(10); }
}

internal sealed class OfflineHandler : HttpMessageHandler
{
    public int Requests;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) { Requests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)); }
}
internal sealed class TestJava(string path) : IJavaLocator
{
    public Task<IReadOnlyList<JavaInstall>> DiscoverAsync(CancellationToken ct=default) => Task.FromResult<IReadOnlyList<JavaInstall>>([new(path,21,"21","Fixture","x64")]);
    public Task<JavaInstall?> ProbeAsync(string path,CancellationToken ct=default) => Task.FromResult<JavaInstall?>(new(path,21,"21","Fixture","x64"));
}
internal sealed class TestLauncher : IProcessLauncher
{
    public int Starts; public LaunchPlan? Plan;
    public IGameProcess Start(LaunchPlan plan,string log,IReadOnlyDictionary<string,string>? env=null) { Starts++; Plan=plan; return new TestProcess(); }
    private sealed class TestProcess : IGameProcess { public int Id=>19442; public bool HasExited=>true; public Task<int> WaitForExitAsync(CancellationToken ct=default)=>Task.FromResult(0); public void Kill(){} public void Dispose(){} }
}
internal sealed class BindingLog : ILogSink
{
    public List<string> Errors {get;}=[];
    public bool IsEnabled(LogEventLevel level,string area)=>level>=LogEventLevel.Warning && area=="Binding";
    public void Log(LogEventLevel level,string area,object? source,string messageTemplate)=>Errors.Add(messageTemplate);
    public void Log(LogEventLevel level,string area,object? source,string messageTemplate,params object?[] values)=>Errors.Add(messageTemplate+" "+string.Join(",",values.Select(v=>v?.ToString())));
}
