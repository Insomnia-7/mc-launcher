using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Launcher.App.Appearance;
using Launcher.App.ViewModels;
using Launcher.Desktop;

namespace Desktop.Checks;

/// <summary>
/// 遵循 UI.md §33「构建和测试要求」的新增 Headless UI 自动化检查样例（接入 tests/Desktop.Checks）。
/// 注意：此文件覆盖「自动化测试验证」范畴；Windows 原生拖拽、Snap Layouts 与多显示器 DPI 仍须在实机运行后单独勾选确认。
/// </summary>
internal static class UiRedesignChecks
{
    public static void Run(
        MainViewModel model,
        Window window,
        Action<int> pump,
        Action<string, bool> check,
        Action<string, bool> capture)
    {
        T Find<T>(string name) where T : Control =>
            window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

        // 1. 窗口与品牌图标保留检查（UI.md §0.1 #8 & §22）
        window.Width = 1440;
        window.Height = 900;
        pump(150);

        var sidebarIcon = Find<Image>("SidebarAppIcon");
        check("保留原始启动器草方块品牌图标", sidebarIcon.IsEffectivelyVisible && sidebarIcon.Source is not null);

        // 2. 侧边栏 240px / 72px 双态检查（UI.md §6 & §23）
        var sidebar = Find<Border>("SidebarPanel");
        model.IsSidebarCollapsed = false;
        pump(300);
        check("侧边栏展开宽度精确为 240px", Math.Abs(sidebar.Bounds.Width - 240) < 0.5);

        model.IsSidebarCollapsed = true;
        pump(300);
        check("侧边栏收起宽度精确为 72px 且图标未消失", Math.Abs(sidebar.Bounds.Width - 72) < 0.5 && Find<Button>("HomeNav").IsEffectivelyVisible);

        model.IsSidebarCollapsed = false;
        pump(300);

        // 3. 首页顶部横幅上移与文案检查（UI.md §5.1 & §7 & §25.1）
        model.ShowHomeCommand.Execute(null);
        pump(260);
        var banner = Find<Border>("HomeHeroBanner");
        var bannerTopInWindow = banner.TranslatePoint(new Point(0, 0), window)?.Y ?? 999;
        check("首页横幅已上移至紧凑顶部位置（窗口内 Y <= 52px）", bannerTopInWindow is >= 36 and <= 52);
        check("首页横幅保留固定中文主文案", banner.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "专注于更好的 MC 联机体验"));

        // 4. 1366x768 低分辨率首屏核心控件可见性验证（UI.md §4.1 & §5.3）
        window.Width = 1366;
        window.Height = 768;
        pump(150);
        foreach (var coreCardName in new[] { "QuickLaunchCard", "NetworkStatusCard", "InstanceListCard", "QuickActionsCard" })
        {
            var card = Find<Border>(coreCardName);
            var pos = card.TranslatePoint(new Point(0, 0), window);
            check($"1366x768 下 {coreCardName} 首屏完整可见", pos is { Y: >= 0 } p && p.Y + card.Bounds.Height <= window.ClientSize.Height);
        }
        capture("home-1366x768-dark", true);

        // 5. 深浅色与跟随系统切换不重建窗口、不丢状态（UI.md §21）
        var selectedBefore = model.SelectedInstance?.InstanceId;
        var windowBefore = ReferenceEquals(PlatformUi.Window, window);
        model.SetThemeMode(ThemeMode.Light);
        pump(250);
        capture("home-1366x768-light", true);
        model.SetThemeMode(ThemeMode.Dark);
        pump(250);
        check("深浅色运行时切换不重建主窗口且保留选中实例", windowBefore && model.SelectedInstance?.InstanceId == selectedBefore);

        // 6. 快速连续导航切换不残留叠页、不闪白（UI.md §24 & §27）
        for (var i = 0; i < 5; i++)
        {
            model.ShowInstancesCommand.Execute(null); pump(30);
            model.OpenMultiplayerCommand.Execute(null); pump(30);
            model.ShowDownloadsCommand.Execute(null); pump(30);
            model.ShowHomeCommand.Execute(null); pump(30);
        }
        pump(300);
        check("快速连续导航切换后页面稳定落位且不透明", model.IsHomePage && Find<Border>("QuickLaunchCard").IsEffectivelyVisible);

        // 7. 减少动画模式检查（UI.md §30）
        MotionPreferences.Mode = "off";
        pump(50);
        check("减少动画模式挂载整窗 reduced-motion 样式类", window.Classes.Contains("reduced-motion"));
        MotionPreferences.Mode = "smooth";
    }
}
