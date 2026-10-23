using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Launcher.App.Appearance;

/// <summary>
/// 让窗口标题栏跟着主题走：Win11 用 DWM 的 caption/border/text 三个颜色属性，
/// Win10 只能退到「深/浅」二选一。取不到句柄或属性不被支持时静默跳过。
/// </summary>
internal static class WindowChrome
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeLegacy = 19;
    private const int DwmBorderColor = 34;
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;

    /// <summary>主窗口，App 在 Show 之后登记进来。</summary>
    public static Window? Target { get; set; }

    public static void Sync(IReadOnlyDictionary<string, string> colors)
    {
        if (Target is null) return;
        var hwnd = new WindowInteropHelper(Target).Handle;
        if (hwnd == IntPtr.Zero) return;

        try
        {
            var bg = Parse(colors, "BgBrush");
            var panel = Parse(colors, "PanelBrush");
            var text = Parse(colors, "TextBrush");
            var line = Parse(colors, "LineBrush");

            // 面板可能是半透明的（玻璃主题），标题栏要的是实色：往 Bg 上压一层再取
            var caption = Flatten(panel, bg);
            var border = Flatten(line, bg);

            var dark = IsDark(bg) ? 1 : 0;
            if (Set(hwnd, DwmUseImmersiveDarkMode, dark) != 0)
                Set(hwnd, DwmUseImmersiveDarkModeLegacy, dark);

            SetColor(hwnd, DwmCaptionColor, caption);
            SetColor(hwnd, DwmBorderColor, border);
            SetColor(hwnd, DwmTextColor, text);
        }
        catch (DllNotFoundException)
        {
            // 非 Windows 或 dwmapi 缺失：标题栏保持系统默认
        }
        catch (Exception)
        {
            // 老系统不认这些属性，忽略
        }
    }

    private static Color Parse(IReadOnlyDictionary<string, string> colors, string key) =>
        colors.TryGetValue(key, out var hex) && hex is { Length: > 0 }
            ? (Color)ColorConverter.ConvertFromString(hex)
            : Colors.Black;

    /// <summary>把半透明色压到不透明底上，得到实色（alpha 混合）。</summary>
    private static Color Flatten(Color top, Color bottom)
    {
        if (top.A == 255) return top;
        var a = top.A / 255d;
        byte Mix(byte t, byte b) => (byte)Math.Round(t * a + b * (1 - a));
        return Color.FromRgb(Mix(top.R, bottom.R), Mix(top.G, bottom.G), Mix(top.B, bottom.B));
    }

    private static bool IsDark(Color color) =>
        (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) < 128;

    private static int Set(IntPtr hwnd, int attribute, int value) =>
        DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));

    /// <summary>DWMWA_*_COLOR 要的是 COLORREF（0x00BBGGRR）。</summary>
    private static int SetColor(IntPtr hwnd, int attribute, Color color)
    {
        var colorref = color.R | (color.G << 8) | (color.B << 16);
        return DwmSetWindowAttribute(hwnd, attribute, ref colorref, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
