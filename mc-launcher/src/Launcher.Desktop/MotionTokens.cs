namespace Launcher.Desktop;

/// <summary>Role-based motion timing, shared by shell, page host and content entrance.</summary>
public static class MotionTokens
{
    public static TimeSpan Hover => TimeSpan.FromMilliseconds(120);
    public static TimeSpan Pressed => TimeSpan.FromMilliseconds(85);
    public static TimeSpan Release => TimeSpan.FromMilliseconds(140);
    public static TimeSpan NavIndicator => TimeSpan.FromMilliseconds(190);
    public static TimeSpan Sidebar => TimeSpan.FromMilliseconds(250);
    public static TimeSpan TextFade => TimeSpan.FromMilliseconds(140);
    public static TimeSpan SidebarTextOut => Hover;
    public static TimeSpan SidebarTextDelay => TimeSpan.FromMilliseconds(80);
    public static TimeSpan PageSwitch => TimeSpan.FromMilliseconds(200);
    public static TimeSpan PageExit => TimeSpan.FromMilliseconds(110);
    public static TimeSpan ContentEntrance => TimeSpan.FromMilliseconds(220);
    public static TimeSpan StaggerStep => TimeSpan.FromMilliseconds(40);
    public static TimeSpan Dialog => TimeSpan.FromMilliseconds(220);
    public static TimeSpan Tooltip => TimeSpan.FromMilliseconds(140);
    public const int TooltipDelayMs = 450;
}
