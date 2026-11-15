using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Launcher.App.ViewModels;
using Launcher.Application.Settings;
using Launcher.Desktop;

internal static class ShellUiChecks
{
    public static void Run(MainViewModel model, MainWindow window, Action<int> pump,
        Action<string, bool> check, Action<string, bool> capture, Action<Control> click, string data, string root)
    {
        T Find<T>(string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(x => x.Name == name);
        var selected = model.SelectedInstance;
        var page = Find<ContentControl>("PageHost").Content;
        var switcher = Find<Button>("ThemeSwitchButton");
        check("标题栏高 38px，保留原始品牌图标", Find<Border>("WindowHeader").Bounds.Height == 38 && Find<Image>("TitleAppIcon").Source is not null);
        check("标题栏显示真实游戏实例页面名称", Find<TextBlock>("TitleEdition").Text == "游戏实例");
        check("标题栏空白使用系统拖动角色", WindowDecorationProperties.GetElementRole(Find<Border>("TitleDragArea")) == WindowDecorationsElementRole.TitleBar);
        check("主题与系统按钮区不触发标题栏拖动", WindowDecorationProperties.GetElementRole(Find<StackPanel>("CaptionButtons")) == WindowDecorationsElementRole.User);
        check("最大化按钮保留原生 Snap 角色", WindowDecorationProperties.GetElementRole(Find<Button>("MaximizeButton")) == WindowDecorationsElementRole.MaximizeButton);
        check("空闲时不显示任务运行提示", !Find<Button>("HeaderTaskButton").IsVisible);
        check("主题快切使用已有业务命令", switcher.Command == model.CycleThemeModeCommand);
        check("主题入口具备键盘焦点与可读说明", switcher.Focusable && Avalonia.Automation.AutomationProperties.GetName(switcher)?.Contains("浅色") == true);

        foreach (var mode in new[] { ThemeMode.System, ThemeMode.Dark, ThemeMode.Light })
        {
            click(switcher); pump(240);
            var key = mode.ToString().ToLowerInvariant();
            check($"标题栏切换到 {key} 并持久保存", model.CurrentThemeMode == mode && AppearanceStore.Read(data).ThemeMode == key);
            check($"切换 {key} 保留实例、页面和窗口", ReferenceEquals(selected, model.SelectedInstance) && ReferenceEquals(page, Find<ContentControl>("PageHost").Content) && window.IsVisible);
            check($"切换 {key} 更新图标提示", ToolTip.GetTip(switcher)?.ToString() == model.ThemeModeTooltip);
        }
        model.IsInstanceSection = false; pump(50);
        check("资源页面标题随导航更新", Find<TextBlock>("TitleEdition").Text == "发现资源");
        model.OpenTaskCenterCommand.Execute(null); pump(50);
        check("任务页面标题随导航更新", Find<TextBlock>("TitleEdition").Text == "下载任务");
        model.OpenWardrobeCommand.Execute(null); pump(50);
        check("角色页面标题随导航更新", Find<TextBlock>("TitleEdition").Text == "角色");
        model.OpenAppearanceCommand.Execute(null); pump(50);
        check("设置页面标题随导航更新", Find<TextBlock>("TitleEdition").Text == "设置");
        model.CloseAppearanceCommand.Execute(null); model.IsExperienceOpen = false; model.IsInstanceSection = true; pump(60);
        model.IsDetailOpen = true; pump(60);
        check("实例详情标题采用真实实例名", Find<TextBlock>("TitleEdition").Text == model.SelectedGameName);
        model.IsDetailOpen = false; pump(40);

        if (OperatingSystem.IsWindows())
            check("Windows 按 Mica、Acrylic、实色顺序申请材质", window.TransparencyLevelHint.SequenceEqual(new[] { WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.None }));
        check("不支持材质时保持实色背景", window.ActualTransparencyLevel != WindowTransparencyLevel.None || (!window.Classes.Contains("mica") && window.Background is ISolidColorBrush { Color.A: 255 }));
        check("内容区始终使用不透明背景", Find<Grid>("PageContent").Background is ISolidColorBrush { Color.A: 255 });
        using (var wallpaper = new WriteableBitmap(new PixelSize(1, 1), new Vector(96, 96)))
        {
            model.WallpaperImage = wallpaper; pump(40);
            check("自定义壁纸停用原生材质", window.TransparencyLevelHint.SequenceEqual(new[] { WindowTransparencyLevel.None }) && !window.Classes.Contains("mica"));
            model.WallpaperImage = null; pump(40);
        }
        foreach (var width in new[] { 1180d, 820d, 760d })
        {
            window.Width = width; window.Height = 620; pump(120);
            check($"{width}px 窗口标题栏按钮完整且不挤占拖动区",
                Find<StackPanel>("CaptionButtons").Parent is Control parent && Find<StackPanel>("CaptionButtons").Bounds.Right <= parent.Bounds.Width + 1 && Find<Border>("TitleDragArea").Bounds.Width > 200);
        }
        var savedName = model.GameDisplayName;
        model.GameDisplayName = new string('长', 80); model.SaveGamePresentationCommand.Execute(null); model.IsDetailOpen = true; pump(80);
        var title = Find<TextBlock>("TitleEdition");
        check("80 字实例名称在窄窗口内省略，不覆盖窗口按钮", title.Text?.Length == 80 && title.TranslatePoint(new Point(title.Bounds.Width, 0), window)!.Value.X <= Find<StackPanel>("CaptionButtons").TranslatePoint(default, window)!.Value.X);
        model.GameDisplayName = savedName; model.SaveGamePresentationCommand.Execute(null); model.IsDetailOpen = false; pump(60);
        window.Width = 1180; window.Height = 800; model.SetThemeMode(ThemeMode.Dark); pump(240);
        capture("shell-dark", true);
        model.SetThemeMode(ThemeMode.Light); pump(240); capture("shell-light", true);
        check("主题切换复用全局背景画刷", ReferenceEquals(window.Background, Avalonia.Application.Current!.Resources["BgBrush"]));
        var outline = Find<Border>("WindowOutline");
        check("窗口细边框不拦截业务点击", outline.IsVisible && !outline.IsHitTestVisible);
        model.SelectedMotion = model.MotionChoices.Single(x => x.Id == "smooth");
        window.WindowState = WindowState.Maximized; pump(25);
        window.WindowState = WindowState.Normal; pump(400);
        check("快速最大化再还原后图标保持正确", Find<Avalonia.Controls.Shapes.Path>("MaximizeGlyph").Data!.ToString() == Geometry.Parse("M 2,2 L 12,2 12,12 2,12 Z").ToString());
        model.SelectedMotion = model.MotionChoices.Single(x => x.Id == "off");
        foreach (var scale in new[] { 1d, 1.25, 1.5, 2 })
        {
            using var image = new RenderTargetBitmap(new PixelSize((int)(window.ClientSize.Width * scale), (int)(window.ClientSize.Height * scale)), new Vector(96 * scale, 96 * scale));
            image.Render(window); image.Save(Path.Combine(root, $"shell-{scale * 100:0}dpi.png"), PngBitmapEncoderOptions.Default);
        }
        check("多倍率输出不改变逻辑标题栏和按钮尺寸", Find<Border>("WindowHeader").Bounds.Height == 38 && Find<Button>("MaximizeButton").Bounds.Size == new Size(42, 32));
    }
}
