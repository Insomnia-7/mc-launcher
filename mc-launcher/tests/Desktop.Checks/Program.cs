using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
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
        // 无头检查不参与单实例互斥：用户开着启动器时，检查进程不能被单实例逻辑直接退掉。
        Environment.SetEnvironmentVariable("FANGYU_ALLOW_MULTI_INSTANCE","1");
        if(args.Contains("--path-probe"))
        {
            AppPaths.EnsureCreated();
            var saved = Environment.GetEnvironmentVariable("FANGYU_PROBE_SAVE");
            bool? accepted = saved is null ? null : AppPaths.TrySetGameRoot(saved);
            Console.WriteLine(JsonSerializer.Serialize(new { AppPaths.GameRoot, AppPaths.IsGameRootConfirmed, GameCreated=Directory.Exists(AppPaths.GameRoot), Accepted=accepted })); return 0;
        }
        var root = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--")) ?? Path.Combine(Path.GetTempPath(), "mclauncher-desktop-checks-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        if(args.Contains("--sidebar-native"))return ShellNativeChecks.Start(root, sidebar: true);
        if(args.Contains("--shell-native"))return ShellNativeChecks.Start(root);
        if(args.Contains("--gpu-native"))return GpuPreviewChecks.Start(root);
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
        var http = new OfflineHandler(); var launch = new TestLauncher(); var installFixture = new VersionInstallFixture(); var resourceFixture = new ResourceFixture(); var appearanceFixture=new AppearanceFixture();var workbenchFixture=new WorkbenchFixture();
        using var services = LauncherServices.Create(s => { s.AddSingleton(new HttpClient(http)); s.AddSingleton<IJavaLocator>(new TestJava(javaPath)); s.AddSingleton<IProcessLauncher>(launch);s.AddSingleton<Launcher.Application.Accounts.IPlayerAppearanceClient>(appearanceFixture);s.AddSingleton<Launcher.Application.Accounts.ISkinService>(appearanceFixture); s.AddSingleton<IVersionMetaClient>(installFixture); s.AddSingleton<ILoaderMetaClient>(installFixture); s.AddSingleton<IDownloader>(installFixture); s.AddSingleton<Launcher.Application.Java.IJavaProvisioner>(installFixture); s.AddSingleton<IForgeInstaller>(installFixture); s.AddSingleton<INeoForgeInstaller>(installFixture); s.AddSingleton<Launcher.Application.Mods.IModrinthClient>(resourceFixture); s.AddSingleton<Launcher.Application.Mods.ICurseForgeClient>(resourceFixture); s.AddSingleton<Launcher.Application.Mods.IResourcePresentationClient>(resourceFixture); s.AddSingleton<IDownloader>(resourceFixture);
            s.AddSingleton(workbenchFixture.Plans);
            if(args.Contains("--multiplayer")) {var fixture=new MultiplayerFixture();s.AddSingleton(fixture);s.AddSingleton<Launcher.Application.Multiplayer.ILanWorldDiscovery>(fixture);s.AddSingleton<Launcher.Application.Multiplayer.IGameServerStatusClient>(fixture);s.AddSingleton<Launcher.Application.Multiplayer.IRoomNetwork>(fixture);}
            if(args.Contains("--live-catalog"))
            {
                var handler=new HttpClientHandler();
                if(Environment.GetEnvironmentVariable("MCLAUNCHER_TEST_PROXY") is {Length:>0} proxy) {handler.Proxy=new WebProxy(proxy);handler.UseProxy=true;}
                var client=new HttpClient(handler){BaseAddress=new Uri("https://api.modrinth.com/v2/"),Timeout=TimeSpan.FromSeconds(20)};
                client.DefaultRequestHeaders.UserAgent.ParseAdd("MCLauncher-UI-Checks/0.2.0");
                s.AddSingleton<Launcher.Application.Mods.IModrinthClient>(new Launcher.Infrastructure.Modrinth.ModrinthClient(client));
                s.AddSingleton<Launcher.Application.Mods.IResourcePresentationClient>(new Launcher.Infrastructure.Meta.ResourcePresentationClient(client,Path.Combine(root,"live-cache")));
            } });
        var clock = Stopwatch.StartNew();
        var model = services.GetRequiredService<MainViewModel>();
        var window = new MainWindow { DataContext = model, Width = 1180, Height = 800 }; PlatformUi.Window = window;
        window.Show(); Pump(120);
        var readyMs = clock.Elapsed.TotalMilliseconds;
        var passed = 0;
        void Check(string name, bool condition) { if (!condition) throw new InvalidOperationException(name); passed++; Console.WriteLine("PASS " + name); }
        T Find<T>(string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
        void Capture(string name, bool resetPointer=true)
        {
            if(resetPointer) { window.MouseMove(new Point(0,0)); window.MouseMove(new Point(0,0)); }
            Pump(220); window.UpdateLayout();
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
            if(args.Contains("--sidebar")){SidebarUiChecks.Run(model,window,Pump,Check,Capture,Click,data);Check("侧边栏无绑定错误",log.Errors.Count==0);Console.WriteLine($"{passed} sidebar checks passed");return 0;}
            if(args.Contains("--shell")){ShellUiChecks.Run(model,window,Pump,Check,Capture,Click,data,root);Check("标题栏与主题无绑定错误",log.Errors.Count==0);Console.WriteLine($"{passed} shell checks passed");return 0;}
            if(args.Contains("--multiplayer")){MultiplayerUiChecks.Run(model,window,services.GetRequiredService<MultiplayerFixture>(),launch,Pump,Check,Capture);Check("联机页面无绑定错误",log.Errors.Count==0);Console.WriteLine($"{passed} multiplayer UI checks passed");return 0;}
            if(args.Contains("--instance-icons")){InstanceIconUiChecks.Run(model,window,Pump,Check,Capture,root);Check("实例图标页面无绑定错误",log.Errors.Count==0);Console.WriteLine($"{passed} instance icon checks passed");return 0;}
            if(args.Contains("--library-storage")){LibraryStorageUiChecks.Run(model,window,Pump,Check,Capture,root,resourceFixture);Check("目录存储页面无绑定错误",log.Errors.Count==0);Console.WriteLine($"{passed} library storage UI checks passed");return 0;}
            if(args.Contains("--skin-preview")){SkinPreviewChecks.Run(root,Check,Pump);return 0;}
            if(args.Contains("--settings-sync")){SettingsSyncUiChecks.Run(model,window,Pump,Check,Capture,root);Check("设置同步与动效无绑定错误",log.Errors.Count==0);Console.WriteLine($"{passed} settings sync checks passed");return 0;}
            if(args.Contains("--pack-upgrade")){ModpackUpgradeUiChecks.Run(model,window,Pump,Check,Capture,root);Check("整合包页面无绑定错误",log.Errors.Count==0);Console.WriteLine($"{passed} pack upgrade checks passed");return 0;}
            if(args.Contains("--launcher-update")){LauncherUpdateUiChecks.Run(window,Pump,Check,Capture);Check("自动更新页面无绑定错误",log.Errors.Count==0);Console.WriteLine($"{passed} launcher update checks passed");return 0;}
            if(args.Contains("--maintenance")){MaintenanceUiChecks.Run(window,Pump,Check,Capture);Check("维护页面无绑定错误",log.Errors.Count==0);Console.WriteLine($"{passed} maintenance checks passed");return 0;}
            if(args.Contains("--workspace")){WorkspaceUiChecks.Run(model,window,Pump,Check,Capture,Click,root);Check("新版工作区无绑定错误",log.Errors.Count==0);Console.WriteLine($"{passed} workspace checks passed");return 0;}
            if(args.Contains("--mod-workbench")){ModWorkbenchUiChecks.Run(model,window,workbenchFixture,Pump,Check,Capture,Click,root);Check("模组工作台无绑定错误",log.Errors.Count==0);Console.WriteLine($"{passed} mod workbench checks passed");return 0;}
            if(args.Contains("--legacy-theme"))
            {
                Check("旧彩色主题迁移为深色主题", !model.IsLightTheme && model.SelectedTheme?.Id=="aurora" && model.ThemeChoices.Count==2);
                var saved=Launcher.Application.Settings.AppearanceStore.Read(data);
                Check("迁移保存深色主题并清除旧配色", saved.ThemeId=="aurora" && saved.CustomTheme is null);
                Check("迁移保留其他外观偏好", saved.MotionMode=="off" && saved.WallpaperOpacity==0.35);
                Check("旧主题迁移后无绑定错误", log.Errors.Count==0);
                return 0;
            }
            if(args.Contains("--empty"))
            {
                Check("首次启动直接进入主页面、不自动弹安装向导", !model.IsSetupOpen && model.Instances.Count==0 && model.ShellPage=="library");
                Check("空游戏库也不阻塞首页联网", http.Requests==0);
                Capture("empty-library");
                // 向导改为手动入口：点「添加游戏」才打开，且内部滚动条不显示突兀的竖条
                model.OpenSetupCommand.Execute(null); Pump(120);
                Check("添加游戏按钮仍可打开安装向导", model.IsSetupOpen);
                var setupScroller = window.GetVisualDescendants().OfType<ScrollViewer>()
                    .FirstOrDefault(s => s.GetVisualAncestors().OfType<Border>().Any(b => b.Classes.Contains("modal-surface")));
                Check("安装向导隐藏纵向滚动条", setupScroller is { VerticalScrollBarVisibility: ScrollBarVisibility.Hidden });
                Capture("first-run");
                model.CloseSetupCommand.Execute(null); Pump(50);
                Check("可以关掉向导后继续使用", !model.IsSetupOpen && !model.HasInstances);
                Check("首次启动页面没有绑定错误", log.Errors.Count==0);
                return 0;
            }
            if(args.Contains("--live-catalog"))
            {
                void WaitLive(Task task) { PumpUntil(()=>task.IsCompleted,45000); if(!task.IsCompleted) throw new TimeoutException("Live request timed out");task.GetAwaiter().GetResult();Pump(100); }
                var livePacks=args.Contains("--live-packs");
                model.IsInstanceSection=false;model.Download.SelectedCategory=model.Download.Categories.First(c=>!c.IsSeparator&&c.Category==(livePacks?Launcher.Domain.Content.DownloadCategory.Modpack:Launcher.Domain.Content.DownloadCategory.Mod));model.Download.Query=livePacks?"Fabulously Optimized":"sodium";
                WaitLive(model.Download.SearchCommand.ExecuteAsync(null));
                Check("真实平台返回项目元数据",model.Download.Results.Any(i=>(livePacks||i.Slug=="sodium")&&i.DownloadsText.Length>0));
                var project=model.Download.Results.First(i=>livePacks||i.Slug=="sodium");PumpUntil(()=>project.IconBytes is {Length:>0},25000);
                Check("真实项目原色图标下载成功",project.IconBytes is {Length:>0});Capture("live-resource-list");
                WaitLive(model.Download.OpenResourceCommand.ExecuteAsync(project));
                if(livePacks)
                {
                    Check("真实整合包介绍页可用",model.Download.ShowsPackOverview&&model.Download.PackDescription.Length>0);Capture("live-pack-overview");
                    model.Download.ShowPackVersionsCommand.Execute(null);Pump(100);
                }
                Check("真实项目版本可按游戏版本浏览",model.Download.ResourceReleaseGroups.Count>0);Capture("live-resource-versions");
                Check("真实项目无绑定错误",log.Errors.Count==0);return 0;
            }
            Check("真实版本目录加载", model.Instances.Count == 3 && model.SelectedInstance is not null);
            Check("外观提供深浅双主题", model.ThemeChoices.Count==2 && model.ThemeChoices.Select(t=>t.Id).SequenceEqual(new[]{"aurora","glacier"}));
            Check("双主题均含非灰阶强调色", model.ThemeChoices.Select(t=>Color.Parse(t.Definition.Colors["AccentBrush"])).All(c=>c.R!=c.G || c.G!=c.B));
            Check("首页构造不联网", http.Requests == 0);
            // 底栏精简：默认「就绪」不占位，右下角 JAVA EDITION 已移除
            Check("默认状态不显示底部状态行", !model.HasStatusText);
            var statusBar = window.GetVisualDescendants().OfType<Border>().First(b => b.Name == "StatusBar");
            Check("底栏不再显示 JAVA EDITION", !statusBar.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "JAVA EDITION"));
            Check("底栏状态行默认隐藏", !statusBar.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == model.StatusText && t.IsEffectivelyVisible));
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
            Click(Find<Button>("SettingsEntry")); Check("设置入口打开外观选项", model.IsPreferencesOpen && Find<ListBox>("ThemeList").IsEffectivelyVisible);
            var preferences=Find<Border>("PreferencesSurface");
            Click(Find<Button>("NetworkSettingsTab"));
            Check("外观与网络在同一设置窗口内切换", model.IsNetworkOpen && !model.IsAppearanceOpen && ReferenceEquals(preferences,Find<Border>("PreferencesSurface")) && !Find<ListBox>("ThemeList").IsEffectivelyVisible);
            model.ProxyModeIndex=1; model.SaveNetworkCommand.Execute(null);
            Check("统一设置保留网络保存功能", Launcher.Application.Settings.ProxySettingsStore.Read(data).Mode==Launcher.Application.Settings.ProxyMode.Direct);
            model.ProxyModeIndex=2; model.ProxyAddress="invalid proxy"; model.SaveNetworkCommand.Execute(null);
            Check("无效代理提示错误且不覆盖已保存配置", model.NetworkHint.Length>0 && Launcher.Application.Settings.ProxySettingsStore.Read(data).Mode==Launcher.Application.Settings.ProxyMode.Direct);
            Capture("settings-network");
            model.CloseTopOverlayCommand.Execute(null); Pump(60);
            Check("统一设置可关闭并恢复页面操作", !model.IsPreferencesOpen && !model.HasOverlay);
            model.OpenNetworkCommand.Execute(null); Pump(50);
            Check("网络诊断快捷入口仍打开统一设置的网络选项", model.IsNetworkOpen && Find<Border>("PreferencesSurface").IsEffectivelyVisible && model.ProxyModeIndex==1);
            Click(Find<Button>("AppearanceSettingsTab"));
            Check("网络返回外观时仅显示一个设置面板", model.IsAppearanceOpen && !model.IsNetworkOpen && model.IsPreferencesOpen);
            Check("主题列表提供深浅双主题且已写盘", model.ThemeChoices.Select(t => t.Id).SequenceEqual(new[]{"aurora","glacier"})
                && File.ReadAllText(Path.Combine(data, "appearance.json")).Contains("glacier"));
            Capture("appearance"); model.CloseAppearanceCommand.Execute(null); Pump(50); Capture("home-dark");
            if(OperatingSystem.IsWindows())
            {
                Check("一体化标题栏保留可缩放窗口", window.ExtendClientAreaToDecorationsHint && window.CanResize && Find<Button>("MaximizeButton").IsEffectivelyVisible);
                Click(Find<Button>("MaximizeButton"));
                Check("最大化后提供还原入口", window.WindowState==WindowState.Maximized && Avalonia.Automation.AutomationProperties.GetName(Find<Button>("MaximizeButton"))=="还原窗口");
                Click(Find<Button>("MaximizeButton"));
                Check("还原按钮恢复普通窗口", window.WindowState==WindowState.Normal);
                Click(Find<Button>("MinimizeButton"));
                Check("标题栏最小化按钮可用", window.WindowState==WindowState.Minimized);
                window.WindowState=WindowState.Normal; window.Activate(); Pump(60);
            }
            Check("标题栏提供三态主题快切", Find<Button>("ThemeSwitchButton").Command == model.CycleThemeModeCommand);
            Check("日间主题已保存", model.IsLightTheme && Launcher.Application.Settings.AppearanceStore.Read(data).ThemeId=="glacier");
            var primaryForeground=(ISolidColorBrush)Avalonia.Application.Current!.Resources["PrimaryTextBrush"]!;
            Check("日间主按钮使用白字", primaryForeground.Color==Colors.White);
            Check("日间矢量图标同步主按钮字色", Find<Button>("LaunchButton").GetVisualDescendants().OfType<UiIcon>().Single().Foreground is ISolidColorBrush { Color:var iconColor } && iconColor==Colors.White);
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
            // 切页是瞬时换内容：任何时候内容区只能挂着当前页，不允许残留上一页或叠页。
            {
                var host = Find<ContentControl>("PageHost");
                var pageTypes = new[] { "LibraryView", "DiscoverView", "ExperienceView", "PreferencesView", "GameDetailView", "MultiplayerView" };
                int AttachedPageCount() => window.GetVisualDescendants().OfType<Control>()
                    .Count(c => pageTypes.Contains(c.GetType().Name));
                model.ShowInstancesCommand.Execute(null); Pump(300);
                for (var i = 0; i < 12; i++)
                {
                    (i % 2 == 0 ? (Action)(() => model.OpenAppearanceCommand.Execute(null)) : () => model.ShowInstancesCommand.Execute(null))();
                    Pump(12);
                }
                model.ShowInstancesCommand.Execute(null); Pump(300);
                var attached = AttachedPageCount();
                Check($"快速连续切页后只挂当前页（页面实例 {attached} 个）",
                    attached == 1 && host.Content is Launcher.Desktop.Views.LibraryView && Math.Abs(host.Opacity - 1) < 0.01);
                Check("切页容器不参与透明度动画", host.Opacity >= 0.99);

                model.OpenAppearanceCommand.Execute(null); Pump(80);
                Check("切到设置后立刻只挂设置页", AttachedPageCount() == 1 && host.Content is Launcher.Desktop.Views.PreferencesView);
                model.ShowInstancesCommand.Execute(null); Pump(80);
                Check("切回游戏库后立刻只挂游戏库", AttachedPageCount() == 1 && host.Content is Launcher.Desktop.Views.LibraryView);
            }

            model.OpenAppearanceCommand.Execute(null); model.CloseAppearanceCommand.Execute(null); model.OpenAccountsCommand.Execute(null); Pump(250);
            Check("快速切换弹窗后可继续操作", model.IsAccountsOpen && !model.IsAppearanceOpen);
            model.CloseAccountsCommand.Execute(null); Pump(250);
            var feedback=Find<Button>("AccountEntry"); var point=feedback.TranslatePoint(new Point(feedback.Bounds.Width/2,feedback.Bounds.Height/2),window)!.Value;
            window.MouseMove(point); window.MouseDown(point,MouseButton.Left); Pump(140);
            // 按下只收回不位移（scale 0.98，83ms），松开用 167ms 弹回 1.0
            Check("按下有收回反馈（scale 0.98）", feedback.IsPressed && Math.Abs(feedback.RenderTransform!.Value.M11-0.98)<0.01);
            window.MouseUp(point,MouseButton.Left); Pump(350); model.CloseAccountsCommand.Execute(null); Pump(180);
            Check("松开后恢复正常尺寸", feedback.RenderTransform!.Value.M11>=0.99);
            var launchButton=Find<Button>("LaunchButton");
            var hoverShadow=launchButton.GetVisualDescendants().OfType<Border>().Single(b=>b.Name=="HoverShadow");
            var ambientShadow=launchButton.GetVisualDescendants().OfType<Border>().Single(b=>b.Name=="AmbientShadow");
            window.MouseMove(new Point(0,0)); Pump(220);
            var launchPoint=launchButton.TranslatePoint(new Point(launchButton.Bounds.Width/2,launchButton.Bounds.Height/2),window)!.Value;
            // 悬停阴影是 180ms 过渡，单次固定采样偶发落在动画首帧（opacity=0）；
            // 这里在 120ms 窗口内寻找中间帧，语义不变但消除时序抖动。
            window.MouseMove(launchPoint);
            var sawFadeIn = false;
            for (var i = 0; i < 12 && !sawFadeIn; i++) { Pump(10); sawFadeIn = hoverShadow.Opacity > 0 && hoverShadow.Opacity < 1; }
            Check("阴影随悬停渐入", sawFadeIn);
            Pump(200); Capture("button-hover",false);
            Check($"悬停结束后阴影稳定（opacity={hoverShadow.Opacity:F2}, over={launchButton.IsPointerOver}）", hoverShadow.Opacity>=0.99);
            // FlClash/M3 式按压：按下瞬间在按钮模板的水波纹层里投一个扩散圆，并有 state layer 压暗。
            // 夹具默认 motionMode=off（减少动效），波纹按设计不画；这里临时开启动画验证后恢复。
            var savedMotionMode = Launcher.Desktop.MotionPreferences.Mode;
            Launcher.Desktop.MotionPreferences.Mode = "smooth"; Pump(40);
            var launchesBeforePress=launch.Starts;
            window.MouseDown(launchPoint,MouseButton.Left); Pump(60);
            var rippleHost = launchButton.GetVisualDescendants().OfType<Canvas>().FirstOrDefault(c => c.Name == "RippleHost");
            var stateLayer = launchButton.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "StateLayer");
            Check("按钮模板提供水波纹层与 state layer", rippleHost is not null && stateLayer is not null);
            Check("按下时出现水波纹圆", rippleHost is not null && rippleHost.Children.OfType<Avalonia.Controls.Shapes.Ellipse>().Any());
            Check("按下时叠加 state layer", stateLayer is { Opacity: > 0 });
            Pump(160);
            Check("按压时阴影收紧且文字保持清晰", ambientShadow.Opacity<=0.2 && hoverShadow.Opacity<0.01 && launchButton.Opacity>0.95);
            // 顺序上这里是 pressed 态：验证「收回」缩放已经生效（松开后由过渡回到 scale(1)）
            Check("主操作按钮按压时同样收回", Math.Abs(launchButton.RenderTransform!.Value.M11 - 0.98) < 0.01);
            Launcher.Desktop.MotionPreferences.Mode = savedMotionMode; Pump(40);
            window.MouseMove(new Point(0,0)); window.MouseUp(new Point(0,0),MouseButton.Left); Pump(250);
            Check("移出后松开不会误启动", launch.Starts==launchesBeforePress && hoverShadow.Opacity<0.01);
            Find<Button>("LibraryNav").Focus(NavigationMethod.Tab); feedback.Focus(NavigationMethod.Tab); Pump(80);
            Check("键盘导航显示焦点框", feedback.IsFocused && feedback.GetVisualDescendants().OfType<Border>().Single(b=>b.Name=="FocusRing").IsVisible);
            feedback.IsEnabled=false; Pump(30); Click(feedback);
            Check("禁用按钮不可触发账号弹窗", !model.IsAccountsOpen && !feedback.IsEffectivelyEnabled && feedback.Opacity<0.6);
            feedback.IsEnabled=true;
            model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="off"); Check("关闭动画立即生效", !MotionPreferences.Enabled && window.Classes.Contains("reduced-motion"));
            window.Width=900; window.Height=640; Pump(60); Capture("home-compact");
            var libraryScroll=Find<ScrollViewer>("LibraryScroll");
            var verticalBar=libraryScroll.GetVisualDescendants().OfType<ScrollBar>().First(b=>b.Orientation==Avalonia.Layout.Orientation.Vertical && b.GetVisualAncestors().OfType<ScrollViewer>().First()==libraryScroll);
            Check("首页滚动条完全隐藏", libraryScroll.VerticalScrollBarVisibility==ScrollBarVisibility.Hidden && !verticalBar.IsVisible);
            var scrollPoint=libraryScroll.TranslatePoint(new Point(60,60),window)!.Value;
            window.MouseMove(scrollPoint); window.MouseWheel(scrollPoint,new Vector(0,-3)); Pump(100);
            Check("隐藏滚动条后仍可用滚轮滚动", libraryScroll.Offset.Y>0);
            libraryScroll.Offset=default; libraryScroll.Focus(NavigationMethod.Tab);
            window.KeyPress(Key.PageDown,RawInputModifiers.None,PhysicalKey.PageDown,null); window.KeyRelease(Key.PageDown,RawInputModifiers.None,PhysicalKey.PageDown,null); Pump(60);
            Check("首页保留键盘翻页", libraryScroll.Offset.Y>0);
            Check("关闭动画也覆盖阴影", hoverShadow.Transitions?.Count==0);
            libraryScroll.Offset=default; Capture("home-compact");
            Check("最小窗口下启动入口仍在窗口内", Find<Button>("LaunchButton").TranslatePoint(new Point(0,0),window) is { X:>=0,Y:>=0 } p && p.X+Find<Button>("LaunchButton").Bounds.Width<=window.ClientSize.Width+1);
            VersionInstallChecks.Run(model, window, installFixture, Pump, Check, Capture, Click);
            var metadataChecks=VersionInstallChecks.CheckMetadataAsync(Check); PumpUntil(()=>metadataChecks.IsCompleted,3000); metadataChecks.GetAwaiter().GetResult();
            ResourceCenterChecks.Run(model,window,resourceFixture,Pump,Check,Capture,Click);
            CatalogScrollChecks.Run(model,window,resourceFixture,Pump,Check,Capture);
            var presentationChecks=ResourceCenterChecks.CheckPresentationCacheAsync(root,Check);PumpUntil(()=>presentationChecks.IsCompleted,5000);presentationChecks.GetAwaiter().GetResult();
            ExperienceUiChecks.Run(model,window,appearanceFixture,resourceFixture,Pump,Check,Capture,root);
            SkinPreviewChecks.Run(root,Check,Pump);
            WorkspaceUiChecks.Run(model,window,Pump,Check,Capture,Click,root);
            ModWorkbenchUiChecks.Run(model,window,workbenchFixture,Pump,Check,Capture,Click,root);
            MaintenanceUiChecks.Run(window,Pump,Check,Capture);
            LauncherUpdateUiChecks.Run(window,Pump,Check,Capture);
            ModpackUpgradeUiChecks.Run(model,window,Pump,Check,Capture,root);
            SettingsSyncUiChecks.Run(model,window,Pump,Check,Capture,root);
            // 删除确认：危险动作的确认按钮要红底白字，其他确认维持主色
            model.SelectedInstance = model.Instances.First(); Pump(80);            model.DeleteInstanceCommand.Execute(null); Pump(150);
            var dangerOk = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("danger-solid"));
            Check("删除版本的确认框弹出且确认按钮带危险样式", model.IsConfirmOpen && dangerOk is not null && dangerOk.Content as string == "删除");
            Check("删除确认按钮红底白字",
                dangerOk!.Background is ISolidColorBrush redFill && redFill.Color == ((ISolidColorBrush)Avalonia.Application.Current!.Resources["DangerFillBrush"]!).Color
                && dangerOk.Foreground is ISolidColorBrush whiteText && whiteText.Color == Colors.White);
            Capture("confirm-delete-danger");
            model.ConfirmCancelCommand.Execute(null); Pump(120);
            Check("取消删除后实例仍在且弹窗关闭", model.Instances.Count >= 3 && !model.IsConfirmOpen
                && !window.GetVisualDescendants().OfType<Button>().Any(b => b.Classes.Contains("danger-solid") && b.IsEffectivelyVisible));
            Check("普通确认按钮不带危险样式", window.GetVisualDescendants().OfType<Button>().Any(b => b.Classes.Contains("primary")));
            // 侧边栏收起/展开：宽度两种状态、文字淡出、状态记忆（大纲 6）
            var sidebar = Find<Border>("SidebarPanel");
            // 收起/展开各有独立按钮（收起态 72px 放不下"图标+按钮"一行，改上下两行），
            // 这里始终点当前可见的那个。
            ToggleButton VisibleSidebarToggle() => window.GetVisualDescendants().OfType<ToggleButton>()
                .First(t => t.Name is "SidebarToggle" or "SidebarToggleCollapsed" && t.IsEffectivelyVisible);
            var sidebarToggle = Find<ToggleButton>("SidebarToggle");
            Check("侧边栏默认展开约 240px", !model.IsSidebarCollapsed && Math.Abs(sidebar.Width - 240) < 1);
            var navText = Find<StackPanel>("SidebarNavText");
            // 必须用真实点击驱动 ToggleButton：代码直接赋 IsChecked 绕过了 Toggle()/Command 调用序列，
            // 测不出「TwoWay 绑定 + Command 双重翻转」这种 blocker（大纲 13.3-1/2）。
            // 收放过程留两张中间帧：宽度是唯一被动画的量，内部布局应随宽度连续重排，不许跳档。
            void CaptureNow(string name)
            {
                window.UpdateLayout();
                using var shot = new RenderTargetBitmap(new PixelSize((int)window.ClientSize.Width,(int)window.ClientSize.Height), new Vector(96,96));
                shot.Render(window); shot.Save(Path.Combine(root, name + ".png"));
            }
            Click(VisibleSidebarToggle());
            CaptureNow("sidebar-collapse-mid");
            Pump(70); CaptureNow("sidebar-collapse-late");
            Pump(350);
            Capture("home-collapsed");
            Check("真实点击后收起：宽度约 72px 且文字淡出", model.IsSidebarCollapsed && Math.Abs(sidebar.Width - 72) < 1 && navText.Opacity < 0.05);
            Check("收起后品牌图标仍显示", Find<Image>("SidebarAppIconCollapsed").IsEffectivelyVisible);
            Check("收起态折叠按钮恢复原触发范围（42×32）",
                Math.Abs(Find<ToggleButton>("SidebarToggleCollapsed").Bounds.Width - 42) < 1
                && Math.Abs(Find<ToggleButton>("SidebarToggleCollapsed").Bounds.Height - 32) < 1);
            Check("收起态折叠按钮不与品牌图标重叠",
                Find<ToggleButton>("SidebarToggleCollapsed").TranslatePoint(new Point(0, 0), sidebar) is { } togglePos
                && Find<Image>("SidebarAppIconCollapsed").TranslatePoint(new Point(0, 0), sidebar) is { } iconPos
                && togglePos.Y >= iconPos.Y + Find<Image>("SidebarAppIconCollapsed").Bounds.Height - 1);
            var libraryNav = Find<Button>("LibraryNav");
            var navCenter = libraryNav.TranslatePoint(new Point(libraryNav.Bounds.Width / 2, libraryNav.Bounds.Height / 2), window)!.Value;
            Check("收起后导航图标仍可点击", navCenter.X > 0 && navCenter.X < sidebar.Bounds.Width + 40);
            Click(VisibleSidebarToggle()); Pump(350);
            Check("真实点击再次展开恢复宽度且文字可见", !model.IsSidebarCollapsed && Math.Abs(sidebar.Width - 240) < 1 && navText.Opacity > 0.9);
            Check("真实点击状态写入外观设置并被读回", !Launcher.Application.Settings.AppearanceStore.Read(data).SidebarCollapsed);
            Check("展开态抑制重复导航提示", ToolTip.GetTip(Find<Button>("LibraryNav")) as string == "游戏实例" && !ToolTip.GetServiceEnabled(Find<Button>("LibraryNav")));

            // 侧边栏四态（大纲 B-1）：窄窗口降为 Minimal，汉堡键开 Overlay 浮层
            var hamburger = Find<Button>("SidebarHamburger");
            window.Width=860; window.Height=760; Pump(220);
            Check("窄窗口自动收起为 Minimal 态", !sidebar.IsEffectivelyVisible && hamburger.IsEffectivelyVisible);
            Capture("home-minimal");
            Click(hamburger); Pump(280);
            Check("汉堡键展开 Overlay 浮层侧栏", sidebar.IsEffectivelyVisible && Math.Abs(sidebar.Width-240)<1 && Find<Border>("SidebarScrim").IsEffectivelyVisible);
            Capture("home-overlay");
            var scrimPoint=new Point(window.ClientSize.Width-80,window.ClientSize.Height-80);
            window.MouseMove(scrimPoint); window.MouseDown(scrimPoint,MouseButton.Left); Pump(40); window.MouseUp(scrimPoint,MouseButton.Left); Pump(220);
            Check("点遮罩收起 Overlay 浮层", !sidebar.IsEffectivelyVisible && !Find<Border>("SidebarScrim").IsEffectivelyVisible);
            window.Width=1180; window.Height=800; Pump(260);
            Check("恢复宽窗口回到 Expanded 态", sidebar.IsEffectivelyVisible && Math.Abs(sidebar.Width-240)<1);
            // 共享蓝色指示条：随 ShellPage 平滑移动（大纲 7）
            var indicator = Find<Border>("NavIndicator");
            Check("首次进入游戏库指示条落位显示", indicator.IsVisible);
            var list = Find<StackPanel>("NavList");
            double IndicatorY() => (indicator.RenderTransform as TranslateTransform)?.Y ?? double.NaN;
            var beforeY = IndicatorY();
            Check("指示条使用强调色", indicator.Background is ISolidColorBrush b && b.Color == ((ISolidColorBrush)Avalonia.Application.Current!.Resources["NavAccentBrush"]!).Color);
            Check("指示条对准游戏库", Math.Abs(beforeY - Find<Button>("LibraryNav").TranslatePoint(new Point(0, 0), list)!.Value.Y - (Find<Button>("LibraryNav").Bounds.Height - indicator.Height) / 2) < 1);
            Find<Button>("MultiplayerNav").Command?.Execute(null); Pump(450);
            Check("切换到联机页指示条平滑移动到新位置",
                model.IsMultiplayerPage && indicator.IsVisible
                && Math.Abs(IndicatorY() - Find<Button>("MultiplayerNav").TranslatePoint(new Point(0, 0), list)!.Value.Y - (Find<Button>("MultiplayerNav").Bounds.Height - indicator.Height) / 2) < 1
                && IndicatorY() != beforeY);
            model.ShowInstancesCommand.Execute(null); Pump(450);
            Check("返回游戏库指示条回到原位", Math.Abs(IndicatorY() - beforeY) < 1);
            Check("无数据绑定错误", log.Errors.Count==0);
            using(var portableServices=LauncherServices.Create(s=>s.RemoveAll<ISecretProtector>()))
            {
                var portableModel=portableServices.GetRequiredService<MainViewModel>();
                Check("缺少 Windows 加密服务时仍能创建跨平台首页", portableModel.Instances.Count>=3);
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
        Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
        do { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); } while(clock.ElapsedMilliseconds<milliseconds);
        Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(1); Dispatcher.UIThread.RunJobs();
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
