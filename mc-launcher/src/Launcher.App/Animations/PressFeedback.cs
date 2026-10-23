using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Launcher.App.Animations;

/// <summary>
/// 按压 / 悬停反馈原语：只动 <see cref="UIElement.RenderTransform"/>，不改布局、不触发重新排版。
/// <para>XAML 一行：<c>&lt;Button anim:PressFeedback.Scale="0.97" anim:PressFeedback.HoverLift="2" /&gt;</c></para>
/// <para>代码一行：<c>PressFeedback.SetScale(button, 0.97);</c></para>
/// </summary>
/// <remarks>
/// 只有显式设置附加属性才会挂事件（默认值不触发回调），所以没写的控件零开销。
/// 缩放原点被设到元素中心（<see cref="UIElement.RenderTransformOrigin"/> = 0.5,0.5），
/// 否则按下去会往右下角缩。
/// </remarks>
public static class PressFeedback
{
    private const int PressDurationMs = 120;
    private const int HoverDurationMs = 150;

    /// <summary>
    /// 按下时的缩放比例（附加属性）。0.97 = 缩到 97%；0 表示关闭；1 表示不缩。
    /// </summary>
    public static readonly DependencyProperty ScaleProperty = DependencyProperty.RegisterAttached(
        "Scale",
        typeof(double),
        typeof(PressFeedback),
        new PropertyMetadata(0.97, OnFeedbackChanged));

    /// <summary>
    /// 悬停时的上移像素（附加属性）。2 = 鼠标移上去整块抬起 2px，离开归位；0 表示关闭。
    /// </summary>
    public static readonly DependencyProperty HoverLiftProperty = DependencyProperty.RegisterAttached(
        "HoverLift",
        typeof(double),
        typeof(PressFeedback),
        new PropertyMetadata(0d, OnFeedbackChanged));

    /// <summary>读取按下缩放比例。</summary>
    public static double GetScale(DependencyObject element) => (double)element.GetValue(ScaleProperty);

    /// <summary>写入按下缩放比例。</summary>
    public static void SetScale(DependencyObject element, double value) => element.SetValue(ScaleProperty, value);

    /// <summary>读取悬停上移像素。</summary>
    public static double GetHoverLift(DependencyObject element) => (double)element.GetValue(HoverLiftProperty);

    /// <summary>写入悬停上移像素。</summary>
    public static void SetHoverLift(DependencyObject element, double value) => element.SetValue(HoverLiftProperty, value);

    private static void OnFeedbackChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        if (element.GetValue(WiredProperty) is true) return;

        element.SetValue(WiredProperty, true);

        // Preview 系列：Button 在冒泡阶段就把 MouseLeftButtonDown 标记为 Handled 了，只有隧道阶段能稳拿到。
        element.PreviewMouseLeftButtonDown += OnPressDown;
        element.PreviewMouseLeftButtonUp += OnPressUp;
        element.MouseEnter += OnMouseEnter;
        element.MouseLeave += OnMouseLeave;
        element.LostMouseCapture += OnLostMouseCapture;
        element.Unloaded += OnUnloaded;
    }

    private static void OnPressDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element) return;

        var scale = GetScale(element);
        if (scale <= 0 || Math.Abs(scale - 1) < 0.001) return;

        var state = MotionCore.Ensure(element);

        // 基值保持 1：HoldEnd 让「按下」这个状态一直保持到松手。
        state.Scale.ScaleX = 1;
        state.Scale.ScaleY = 1;

        var duration = TimeSpan.FromMilliseconds(PressDurationMs);
        state.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scale, duration)
        {
            EasingFunction = MotionCore.EaseOut(),
            FillBehavior = FillBehavior.HoldEnd,
        });
        state.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scale, duration)
        {
            EasingFunction = MotionCore.EaseOut(),
            FillBehavior = FillBehavior.HoldEnd,
        });
    }

    private static void OnPressUp(object sender, MouseButtonEventArgs e) => Release(sender);

    private static void OnLostMouseCapture(object sender, MouseEventArgs e) => Release(sender);

    private static void OnMouseLeave(object sender, MouseEventArgs e)
    {
        Release(sender);
        Lift(sender, lifted: false);
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        if (MotionCore.TryGet(element) is not { } state) return;

        // 元素被移出树（切页）时把动画清干净，避免下次进来从半截状态起步。
        state.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        state.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        state.Translate.BeginAnimation(TranslateTransform.YProperty, null);
        state.Scale.ScaleX = 1;
        state.Scale.ScaleY = 1;
        state.Translate.Y = 0;
    }

    private static void OnMouseEnter(object sender, MouseEventArgs e) => Lift(sender, lifted: true);

    private static void Release(object? sender)
    {
        if (sender is not FrameworkElement element) return;
        if (MotionCore.TryGet(element) is not { } state) return;
        if (GetScale(element) <= 0) return;

        // 基值写成终值 1，动画从当前值（0.97）回到 1，Stop 一结束就回落到基值 = 1，没有残留。
        state.Scale.ScaleX = 1;
        state.Scale.ScaleY = 1;

        var duration = TimeSpan.FromMilliseconds(PressDurationMs);
        state.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, duration)
        {
            EasingFunction = MotionCore.EaseOut(),
            FillBehavior = FillBehavior.Stop,
        });
        state.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, duration)
        {
            EasingFunction = MotionCore.EaseOut(),
            FillBehavior = FillBehavior.Stop,
        });
    }

    private static void Lift(object? sender, bool lifted)
    {
        if (sender is not FrameworkElement element) return;

        var lift = GetHoverLift(element);
        if (lift <= 0) return;

        var state = MotionCore.Ensure(element);
        var target = lifted ? -lift : 0;
        var token = ++state.Token;

        // 基值永远是 0（静止位）。用 HoldEnd 把「悬停抬起」保持住，
        // 千万别写成「先把基值设成 -lift 再动画」—— 那会瞬间跳到位，动画根本没机会跑。
        state.Translate.Y = 0;

        var animation = new DoubleAnimation(target, TimeSpan.FromMilliseconds(HoverDurationMs))
        {
            EasingFunction = MotionCore.EaseOut(),
            FillBehavior = FillBehavior.HoldEnd,
        };

        animation.Completed += (_, _) =>
        {
            // 归位动画播完就把时钟摘掉，让基值 0 生效；悬停态则保留 HoldEnd，值停在 -lift。
            if (lifted || state.Token != token) return;

            state.Translate.BeginAnimation(TranslateTransform.YProperty, null);
            state.Translate.Y = 0;
        };

        state.Translate.BeginAnimation(TranslateTransform.YProperty, animation);
    }

    private static readonly DependencyProperty WiredProperty = DependencyProperty.RegisterAttached(
        "Wired",
        typeof(bool),
        typeof(PressFeedback),
        new PropertyMetadata(false));
}
