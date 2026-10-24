using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Launcher.App.ViewModels;
using Launcher.Application.Settings;

internal static partial class UiCheck
{
    private const int DwmCaptionColor = 35;
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeLegacy = 19;

    /// <summary>
    /// 标题栏颜色必须真的落到目标值。插值靠 DispatcherTimer 推进，
    /// 只要有一轮 Tick 被吞（最小化、前台被抢），颜色就会停在中间色并一直留着。
    /// 这里直接问 DWM 要实际生效的 caption 色，而不是信任我们自己的内部状态。
    /// </summary>
    private static void CheckCaptionMatchesTheme(Window window, MainViewModel vm, string label)
    {
        // 测试窗口不是 App 启动时那个，先把 WindowChrome 指过来，否则属性压根没往它身上写。
        // 先把窗口登记给 WindowChrome，再切一次主题触发 Sync —— 否则读的是「上一次
        // 切换时 Target 还指向别的窗口」留下的状态，测了个寂寞。
        Launcher.App.Appearance.WindowChrome.Target = window;
        var previous = vm.SelectedTheme;
        vm.SelectedTheme = vm.ThemeChoices.First(t => t.Id != previous?.Id);
        var hwnd = new WindowInteropHelper(window).Handle;
        var colors = ThemeCatalog.Resolve(vm.SelectedTheme!.Definition);
        var bg = (Color)ColorConverter.ConvertFromString(colors["BgBrush"]);
        var panel = (Color)ColorConverter.ConvertFromString(colors["PanelBrush"]);
        var expected = Flatten(panel, bg);

        Pump(Task.Delay(1200));   // 过渡 380ms + 兜底 600ms，留足余量

        var hr=TryReadCaption(hwnd, out var actual);
        if (hr != 0)
        {
            // DWMWA_CAPTION_COLOR（35）要 Win11 22H2+；这台机器返回 E_INVALIDARG，
            // 说明标题栏只能靠「深/浅」模式跟随主题——那就用那个属性验，别伪造通过。
            Console.WriteLine($"[chrome] 本机不支持 DWMWA_CAPTION_COLOR（hr=0x{hr:X8}），改验深/浅模式");
            CheckDarkModeMatchesTheme(hwnd, bg, label);
            return;
        }

        var ok = Math.Abs(actual.R - expected.R) <= 2
                 && Math.Abs(actual.G - expected.G) <= 2
                 && Math.Abs(actual.B - expected.B) <= 2;
        if (!ok) Console.WriteLine($"[chrome] 「{label}」期望 {expected} 实际 {actual}");
        Check(label, ok);
    }

    /// <summary>Win10 1809+ 都认的 DWMWA_USE_IMMERSIVE_DARK_MODE（20）：主题底色是深的就必须是 1。</summary>
    private static void CheckDarkModeMatchesTheme(IntPtr hwnd, Color background, string label)
    {
        // Win11 用 20，Win10（1809 前后）只有旧编号 19；两个都读，任一对得上就算过。
        var expectDark = (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) < 128 ? 1 : 0;
        var observed = new System.Collections.Generic.List<string>();
        foreach (var attribute in new[] { DwmUseImmersiveDarkMode, DwmUseImmersiveDarkModeLegacy })
        {
            var hr = DwmGetWindowAttribute(hwnd, attribute, out var mode, sizeof(int));
            observed.Add(hr == 0 ? $"{attribute}={mode}" : $"{attribute}=err");
            if (hr == 0 && mode == expectDark) { Check(label, true); return; }
        }
        Console.WriteLine($"[chrome] 「{label}」深色模式期望 {expectDark}，实测 {string.Join(", ", observed)}");
        Check(label, false);
    }

    private static Color Flatten(Color top, Color bottom)
    {
        if (top.A == 255) return top;
        var a = top.A / 255d;
        byte Mix(byte t, byte b) => (byte)Math.Round(t * a + b * (1 - a));
        return Color.FromRgb(Mix(top.R, bottom.R), Mix(top.G, bottom.G), Mix(top.B, bottom.B));
    }

    private static int TryReadCaption(IntPtr hwnd, out Color color)
    {
        color = Colors.Black;
        if (hwnd == IntPtr.Zero) return unchecked((int)0x80070006);   // E_HANDLE
        var hr = DwmGetWindowAttribute(hwnd, DwmCaptionColor, out var colorref, sizeof(int));
        if (hr != 0) return hr;
        // COLORREF 是 0x00BBGGRR
        color = Color.FromRgb((byte)(colorref & 0xFF), (byte)((colorref >> 8) & 0xFF), (byte)((colorref >> 16) & 0xFF));
        return 0;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
