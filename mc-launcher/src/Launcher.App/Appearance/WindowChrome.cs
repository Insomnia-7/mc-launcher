using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Launcher.App.Appearance;

/// <summary>
/// 让窗口标题栏跟着主题走：Win11 用 DWM 的 caption/border/text 三个颜色属性，
/// Win10 只能退到「深/浅」二选一。取不到句柄或属性不被支持时静默跳过。
/// 切主题时按帧插值，避免标题栏「啪」地一下换色。
/// </summary>
internal static class WindowChrome
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeLegacy = 19;
    private const int DwmBorderColor = 34;
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;

    private static DispatcherTimer? _timer;
    private static Color _caption;
    private static Color _border;
    private static Color _text;
    private static bool _primed;

    /// <summary>主窗口，App 在 Show 之后登记进来。</summary>
    public static Window? Target { get; set; }

    public static void Sync(IReadOnlyDictionary<string, string> colors) => Sync(colors, TimeSpan.Zero);

    /// <summary>transition 大于 0 时按 16ms 一帧把标题栏颜色推到目标值。</summary>
    public static void Sync(IReadOnlyDictionary<string, string> colors, TimeSpan transition)
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

            if (!_primed || transition <= TimeSpan.Zero || _timer is { IsEnabled: true })
            {
                _caption = caption; _border = border; _text = text;
                Apply(hwnd);
                _primed = true;
                return;
            }

            var started = DateTime.UtcNow;
            var fromCaption = _caption;
            var fromBorder = _border;
            var fromText = _text;

            _timer?.Stop();
            var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            _timer = timer;
            timer.Tick += (_, _) =>
            {
                // 标题栏插值也靠 DispatcherTimer：窗口最小化时这个计时器仍在跑，
                // 但系统会停掉 DWM 之外的渲染；不要在这里假设「一定跑满 frames 次」，
                // 用真实经过时间推进，最小化再恢复也不会停在中间色。
                var elapsed = DateTime.UtcNow - started;
                var t = transition.TotalMilliseconds <= 0 ? 1d : Math.Min(1d, elapsed.TotalMilliseconds / transition.TotalMilliseconds);
                var eased = 1 - Math.Pow(1 - t, 3);   // easeOutCubic
                _caption = Lerp(fromCaption, caption, eased);
                _border = Lerp(fromBorder, border, eased);
                _text = Lerp(fromText, text, eased);
                Apply(hwnd);
                if (t >= 1d)
                {
                    timer.Stop();
                    _caption = caption; _border = border; _text = text;
                    Apply(hwnd);
                }
            };
            timer.Start();
            // 兜底：前台被抢占、窗口被最小化到托盘时 Tick 可能长时间不来，
            // 到点直接落到目标色，避免标题栏一直是半途的中间色。
            var guard = new DispatcherTimer { Interval = transition + TimeSpan.FromMilliseconds(600) };
            guard.Tick += (_, _) =>
            {
                guard.Stop();
                timer.Stop();
                _caption = caption; _border = border; _text = text;
                Apply(hwnd);
            };
            guard.Start();
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

    private static void Apply(IntPtr hwnd)
    {
        SetColor(hwnd, DwmCaptionColor, _caption);
        SetColor(hwnd, DwmBorderColor, _border);
        SetColor(hwnd, DwmTextColor, _text);
    }

    private static Color Lerp(Color from, Color to, double t) => Color.FromArgb(
        (byte)Math.Round(from.A + (to.A - from.A) * t),
        (byte)Math.Round(from.R + (to.R - from.R) * t),
        (byte)Math.Round(from.G + (to.G - from.G) * t),
        (byte)Math.Round(from.B + (to.B - from.B) * t));

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
