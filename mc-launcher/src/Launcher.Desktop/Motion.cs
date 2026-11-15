using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Styling;

namespace Launcher.Desktop;

/// <summary>
/// 通用动效时长，以及 UI 重构计划中已落地阶段的专用节奏。
/// 这里改一个数，全项目动效跟着变；「关闭动画」不在这里生效——
/// 关掉动效靠窗口上的 reduced-motion 类统一清空 Transitions，见 Styles/Controls.axaml。
/// </summary>
public static class MotionTiming
{
    /// <summary>颜色、悬停、按下、文字消失、窗口控制按钮。</summary>
    public const int FasterMs = 83;
    /// <summary>小位移、图标交换、失焦降灰、文字出现。</summary>
    public const int FastMs = 167;
    /// <summary>侧边栏、页面、设置面板。</summary>
    public const int NormalMs = 250;
    /// <summary>卡片首次入场。</summary>
    public const int SlowMs = 420;

    /// <summary>带内元素的错峰间隔。</summary>
    public const int StaggerMs = 40;

    // UI redesign M4: text phases fit inside the 250ms sidebar width transition.
    public const int SidebarTextOutMs = 120;
    public const int SidebarTextDelayMs = 80;
    public const int SidebarTextInMs = 140;
    public static TimeSpan Navigation => TimeSpan.FromMilliseconds(190);

    /// <summary>
    /// 大纲在 §3.2 B-1 与 §3.4 里单独点名了 200ms（浮层滑入、设置退场）。
    /// 这是四档之外唯一被写明的例外，集中在这里；别的地方不要出现第五个数字。
    /// </summary>
    public const int PanelMs = 200;

    public static TimeSpan Faster => TimeSpan.FromMilliseconds(FasterMs);
    public static TimeSpan Fast => TimeSpan.FromMilliseconds(FastMs);
    public static TimeSpan Normal => TimeSpan.FromMilliseconds(NormalMs);
    public static TimeSpan Slow => TimeSpan.FromMilliseconds(SlowMs);
    public static TimeSpan Panel => TimeSpan.FromMilliseconds(PanelMs);

    /// <summary>主题换色走颜色过渡，用 Normal 档。</summary>
    public static TimeSpan Theme => Normal;
}

/// <summary>
/// 标准 cubic-bezier 求值。Avalonia 自带的缓动只到 CubicEaseOut 一档，
/// 而队伍规范要求进场与退场必须用两条不同的基线曲线，所以这里按 CSS 的定义算。
/// </summary>
public class CubicBezierEasing(double x1, double y1, double x2, double y2) : Easing
{
    private const double Epsilon = 1e-6;

    private static double SampleX(double t, double p1, double p2) => 3 * (1 - t) * (1 - t) * t * p1 + 3 * (1 - t) * t * t * p2 + t * t * t;
    private static double SampleY(double t, double p1, double p2) => 3 * (1 - t) * (1 - t) * t * p1 + 3 * (1 - t) * t * t * p2 + t * t * t;
    private static double SampleDerivativeX(double t, double p1, double p2) => 3 * (1 - t) * (1 - t) * p1 + 6 * (1 - t) * t * (p2 - p1) + 3 * t * t * (1 - p2);

    public override double Ease(double progress)
    {
        if (progress <= 0) return 0;
        if (progress >= 1) return 1;
        var parameter = SolveParameter(progress);
        return SampleY(parameter, y1, y2);
    }

    private double SolveParameter(double x)
    {
        var t = x;
        for (var i = 0; i < 8; i++)
        {
            var error = SampleX(t, x1, x2) - x;
            if (Math.Abs(error) < Epsilon) return t;
            var derivative = SampleDerivativeX(t, x1, x2);
            if (Math.Abs(derivative) < Epsilon) break;
            t -= error / derivative;
            if (t is < 0 or > 1) break;
        }
        double low = 0, high = 1;
        t = x;
        for (var i = 0; i < 32; i++)
        {
            var value = SampleX(t, x1, x2);
            if (Math.Abs(value - x) < Epsilon) return t;
            if (value < x) low = t; else high = t;
            t = (low + high) / 2;
        }
        return t;
    }
}

/// <summary>进场：cubic-bezier(0,0,0,1)，快出慢入。</summary>
public sealed class EntranceEasing() : CubicBezierEasing(0, 0, 0, 1);

/// <summary>退场：cubic-bezier(1,0,1,1)，慢出快入，并且永远比进场短。</summary>
public sealed class ExitEasing() : CubicBezierEasing(1, 0, 1, 1);

/// <summary>曲线只有三条（设计大纲 §4.2）。禁止 ease / ease-in-out / ease-out 与任何回弹曲线。</summary>
public static class MotionEasing
{
    public static Easing Entrance { get; } = new EntranceEasing();
    public static Easing Exit { get; } = new ExitEasing();
    public static Easing Linear { get; } = new LinearEasing();

    /// <summary>按「方向」取曲线：进场用 Entrance，退场用 Exit。</summary>
    public static Easing For(bool entering) => entering ? Entrance : Exit;
}

public static class MotionPreferences
{
    private static string _mode = "smooth";
    public static event Action? Changed;
    public static string Mode { get => _mode; set { if (_mode == value) return; _mode = value; Changed?.Invoke(); } }

    /// <summary>三档开关的唯一判定点：流畅 / 跟随系统 / 关闭。</summary>
    public static bool Enabled => Mode == "smooth" || (Mode == "system" && SystemAnimationsEnabled());

    private const uint GetClientAreaAnimation = 0x1042;

    private static bool SystemAnimationsEnabled() =>
        !OperatingSystem.IsWindows() || !SystemParametersInfo(GetClientAreaAnimation, 0, out var enabled, 0) || enabled;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, [MarshalAs(UnmanagedType.Bool)] out bool value, uint flags);
}

/// <summary>
/// 通用的合成层动效。只动物理属性里的 Transform 与 Opacity，永远不碰 Width/Height 与阴影模糊半径。
/// </summary>
public static class Motion
{
    public static Task AnimateOpacity(Visual target, double from, double to, int milliseconds, Easing easing, CancellationToken token)
    {
        if (milliseconds <= 0 || Math.Abs(from - to) < 0.0001) { target.Opacity = to; return Task.CompletedTask; }
        var animation = new Animation { Duration = TimeSpan.FromMilliseconds(milliseconds), Easing = easing, FillMode = FillMode.Forward };
        animation.Children.Add(new KeyFrame { Cue = new Cue(0), Setters = { new Setter(Visual.OpacityProperty, from) } });
        animation.Children.Add(new KeyFrame { Cue = new Cue(1), Setters = { new Setter(Visual.OpacityProperty, to) } });
        return animation.RunAsync(target, token);
    }

    public static Task AnimateOpacity(Visual target, double from, double to, TimeSpan duration, Easing easing, CancellationToken token) =>
        AnimateOpacity(target, from, to, (int)duration.TotalMilliseconds, easing, token);

    /// <summary>对任意 double 依赖属性做补间（例如 IconLabel.TextOpacity）。</summary>
    public static Task AnimateDouble(Animatable target, AvaloniaProperty property, double from, double to, int milliseconds, Easing easing, CancellationToken token)
    {
        if (milliseconds <= 0 || Math.Abs(from - to) < 0.0001) { target.SetValue(property, to); return Task.CompletedTask; }
        var animation = new Animation { Duration = TimeSpan.FromMilliseconds(milliseconds), Easing = easing, FillMode = FillMode.Forward };
        animation.Children.Add(new KeyFrame { Cue = new Cue(0), Setters = { new Setter(property, from) } });
        animation.Children.Add(new KeyFrame { Cue = new Cue(1), Setters = { new Setter(property, to) } });
        return animation.RunAsync(target, token);
    }

    public static Task AnimateTranslateX(Control target, double from, double to, int milliseconds, Easing easing, CancellationToken token)
    {
        if (target.RenderTransform is not TranslateTransform transform) { transform = new TranslateTransform(0, 0); target.RenderTransform = transform; }
        if (milliseconds <= 0 || Math.Abs(from - to) < 0.0001) { transform.X = to; return Task.CompletedTask; }
        transform.X = to;
        var animation = new Animation { Duration = TimeSpan.FromMilliseconds(milliseconds), Easing = easing, FillMode = FillMode.Forward };
        animation.Children.Add(new KeyFrame { Cue = new Cue(0), Setters = { new Setter(TranslateTransform.XProperty, from) } });
        animation.Children.Add(new KeyFrame { Cue = new Cue(1), Setters = { new Setter(TranslateTransform.XProperty, to) } });
        return animation.RunAsync(target, token);
    }

    public static Task AnimateTranslateX(Control target, double from, double to, TimeSpan duration, Easing easing, CancellationToken token) =>
        AnimateTranslateX(target, from, to, (int)duration.TotalMilliseconds, easing, token);

    public static Task AnimateTranslateY(Control target, double from, double to, int milliseconds, Easing easing, CancellationToken token)
    {
        if (target.RenderTransform is not TranslateTransform transform) { transform = new TranslateTransform(0, 0); target.RenderTransform = transform; }
        if (milliseconds <= 0 || Math.Abs(from - to) < 0.0001) { transform.Y = to; return Task.CompletedTask; }
        transform.Y = to;
        var animation = new Animation { Duration = TimeSpan.FromMilliseconds(milliseconds), Easing = easing, FillMode = FillMode.Forward };
        animation.Children.Add(new KeyFrame { Cue = new Cue(0), Setters = { new Setter(TranslateTransform.YProperty, from) } });
        animation.Children.Add(new KeyFrame { Cue = new Cue(1), Setters = { new Setter(TranslateTransform.YProperty, to) } });
        return animation.RunAsync(target, token);
    }

    public static Task AnimateTranslateY(Control target, double from, double to, TimeSpan duration, Easing easing, CancellationToken token) =>
        AnimateTranslateY(target, from, to, (int)duration.TotalMilliseconds, easing, token);

    public static Task AnimateScale(Control target, double from, double to, int milliseconds, Easing easing, CancellationToken token)
    {
        if (target.RenderTransform is not ScaleTransform transform) { transform = new ScaleTransform(1, 1); target.RenderTransform = transform; }
        if (milliseconds <= 0 || Math.Abs(from - to) < 0.0001) { transform.ScaleX = to; transform.ScaleY = to; return Task.CompletedTask; }
        transform.ScaleX = to;
        transform.ScaleY = to;
        var animation = new Animation { Duration = TimeSpan.FromMilliseconds(milliseconds), Easing = easing, FillMode = FillMode.Forward };
        animation.Children.Add(new KeyFrame { Cue = new Cue(0), Setters = { new Setter(ScaleTransform.ScaleXProperty, from), new Setter(ScaleTransform.ScaleYProperty, from) } });
        animation.Children.Add(new KeyFrame { Cue = new Cue(1), Setters = { new Setter(ScaleTransform.ScaleXProperty, to), new Setter(ScaleTransform.ScaleYProperty, to) } });
        return animation.RunAsync(target, token);
    }

    /// <summary>
    /// 列表错峰入场（设计大纲 §3.3 C-3）：每项 opacity 0→1、translateY 12px→0，
    /// 单次 420ms，项间 40ms，只在首次进入播放。宿主统一调度，逐项写会各写一套时长且无法统一停。
    /// </summary>
    public static async Task PlayStaggerAsync(IReadOnlyList<Control> items, CancellationToken token)
    {
        if (!MotionPreferences.Enabled)
        {
            foreach (var item in items) { item.Opacity = 1; item.RenderTransform = TransformOperations.Identity; }
            return;
        }
        var running = new List<Task>();
        try
        {
            for (var index = 0; index < items.Count; index++) running.Add(StaggeredItem(items[index], index * MotionTiming.StaggerMs, token));
            await Task.WhenAll(running);
        }
        catch (OperationCanceledException) { }
    }

    private static async Task StaggeredItem(Control item, int delayMs, CancellationToken token)
    {
        item.Opacity = 0;
        if (delayMs > 0) await Task.Delay(delayMs, token);
        await Task.WhenAll(
            AnimateOpacity(item, 0, 1, MotionTiming.SlowMs, MotionEasing.Entrance, token),
            AnimateTranslateY(item, 12, 0, MotionTiming.SlowMs, MotionEasing.Entrance, token));
    }
}
