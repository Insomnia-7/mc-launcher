using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace Launcher.Desktop.Controls;

/// <summary>
/// Material 3 风格的水波纹按压反馈（FlClash 同款观感）：从触点扩散一个圆形，
/// 边扩散边淡出。纯 Avalonia 实现——模板里留一个 <c>Canvas Name="RippleHost"</c>，
/// 这里在按下时向其中投一个 <see cref="Ellipse"/> 并驱动缩放/透明度动画。
/// 尊重「减少动态效果」：关闭动画时直接不画。
/// </summary>
public static class Ripple
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Button, bool>("IsEnabled", typeof(Ripple));

    public static bool GetIsEnabled(Button button) => button.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(Button button, bool value) => button.SetValue(IsEnabledProperty, value);

    /// <summary>波纹扩散时长，接近 M3 的 400–500ms。</summary>
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(430);

    private static bool _installed;

    /// <summary>
    /// 注册类级处理器：对所有 Button 生效，与实例创建/样式应用时机无关。
    /// 由 <c>App</c> 在初始化早期调用一次（也由静态构造自动调用）。
    /// </summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        InputElement.PointerPressedEvent.AddClassHandler<Button>(OnPointerPressed, RoutingStrategies.Tunnel);
    }

    static Ripple() => Install();

    private static void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {

        if (sender is not Button button || !button.IsEffectivelyEnabled) return;
        if (!GetIsEnabled(button)) return;
        if (!e.GetCurrentPoint(button).Properties.IsLeftButtonPressed) return;
        if (!MotionPreferences.Enabled) return;

        var host = button.GetVisualDescendants().OfType<Canvas>().FirstOrDefault(c => c.Name == "RippleHost");
        if (host is null || host.Bounds.Width <= 0 || host.Bounds.Height <= 0) return;

        var point = e.GetPosition(host);
        var width = host.Bounds.Width;
        var height = host.Bounds.Height;

        // 半径取到最远角的距离，保证圆最终盖满整个按钮。
        static double Distance(double dx, double dy) => Math.Sqrt(dx * dx + dy * dy);
        var radius = Math.Max(
            Math.Max(Distance(point.X, point.Y), Distance(width - point.X, point.Y)),
            Math.Max(Distance(point.X, height - point.Y), Distance(width - point.X, height - point.Y)));
        var diameter = radius * 2;

        // M3：波纹用按钮的前景色（浅色按钮深色波纹、深色按钮浅色波纹）。
        var fill = button.Foreground as IBrush ?? new SolidColorBrush(Colors.Black);
        var scale = new ScaleTransform(0, 0);
        var ellipse = new Ellipse
        {
            Width = diameter,
            Height = diameter,
            Fill = fill,
            Opacity = 0.26,
            IsHitTestVisible = false,
            RenderTransform = scale,
            // 以圆心（触点）为缩放原点，圆从触点向外扩散。
            RenderTransformOrigin = RelativePoint.Center,
        };
        Canvas.SetLeft(ellipse, point.X - diameter / 2);
        Canvas.SetTop(ellipse, point.Y - diameter / 2);

        // Animation.RunAsync 对非 Visual 的 Animatable（ScaleTransform）会内部强转 Visual 抛异常，
        // 因此这里用 Transitions 驱动：下一帧把目标值设上去，由属性系统插值。
        ellipse.Transitions = new Transitions
        {
            new DoubleTransition { Property = Visual.OpacityProperty, Duration = Duration, Easing = new CubicEaseOut() },
        };
        scale.Transitions = new Transitions
        {
            new DoubleTransition { Property = ScaleTransform.ScaleXProperty, Duration = Duration, Easing = new CubicEaseOut() },
            new DoubleTransition { Property = ScaleTransform.ScaleYProperty, Duration = Duration, Easing = new CubicEaseOut() },
        };
        host.Children.Add(ellipse);

        _ = FadeOutAsync(host, ellipse, scale);
    }

    /// <summary>把波纹推到满尺寸并淡出，结束后从绘制层移除。</summary>
    private static async Task FadeOutAsync(Canvas host, Ellipse ellipse, ScaleTransform scale)
    {
        try
        {
            // 等一帧让初始值先落位，Transitions 才会真正插值而不是直接跳到终值。
            await Task.Delay(16);
            scale.ScaleX = 1;
            scale.ScaleY = 1;
            ellipse.Opacity = 0;
            await Task.Delay(Duration + TimeSpan.FromMilliseconds(40));
        }
        catch (Exception)
        {
            // 窗口关闭 / 控件移除时静默清理即可。
        }
        finally
        {
            host.Children.Remove(ellipse);
        }
    }
}
