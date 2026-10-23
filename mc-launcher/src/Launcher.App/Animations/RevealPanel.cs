using System.Windows;
using System.Windows.Media.Animation;

namespace Launcher.App.Animations;

/// <summary>
/// 展开 / 收起原语：动画 <see cref="FrameworkElement.MaxHeight"/> 把一块内容「拉」出来或「卷」回去。
/// <para>代码一行：<c>RevealPanel.Expand(details);</c> / <c>RevealPanel.Collapse(details);</c></para>
/// <para>XAML 里不用写任何东西，元素平时保持 <c>Visibility="Collapsed"</c> 即可。</para>
/// </summary>
/// <remarks>
/// <para>为什么用 MaxHeight 而不是 Height：Height 一旦写死，内容换行/字体变化就切边；
/// MaxHeight 是上限，展开结束后置为 <see cref="double.PositiveInfinity"/>，内容仍能自适应。</para>
/// <para>可打断：连续点两次会直接顶掉上一段动画（<see cref="UIElement.BeginAnimation(DependencyProperty, AnimationTimeline)"/> 语义），
/// 每次动画带一个序号，过期的 Completed 回调不会再去写终值。</para>
/// <para>副作用：MaxHeight 是布局属性，动画期间每一帧都会重新测量；只适合「一块内容」级别的展开，
/// 别拿它做整页动画（那种场景用 <see cref="PageTransition"/>）。</para>
/// </remarks>
public static class RevealPanel
{
    private const int DefaultDurationMs = 180;

    /// <summary>
    /// 展开：MaxHeight 从 0（或当前高度）动到内容实际高度，结束后置为无限大。
    /// </summary>
    /// <param name="element">要展开的元素（通常是包住内容的 Border / StackPanel）。</param>
    /// <param name="durationMs">时长（毫秒），&lt;=0 时回落到 180。</param>
    public static void Expand(FrameworkElement element, int durationMs = DefaultDurationMs)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (durationMs <= 0) durationMs = DefaultDurationMs;

        var state = StateOf(element);
        var token = ++state.Token;

        element.Visibility = Visibility.Visible;

        // MaxHeight 只是限制测量高度，超出部分照样画出来；开裁剪才能看见「卷」的过程。
        element.ClipToBounds = true;

        // 先记下当前限制（可能是上一段动画的中途值），再把基值写成终值：
        // 基值 = 终值，FillBehavior.Stop 的动画一结束就回落到它，天然没有残留。
        // 同时这一步也解除了测量限制 —— 否则元素带着 MaxHeight=0 去 Measure，量出来的高度永远是 0。
        var current = element.MaxHeight;
        element.MaxHeight = double.PositiveInfinity;

        var target = MeasureHeight(element);
        if (target <= 0.5)
        {
            // 还没有内容（或不在可视树里）：直接放开，别播一段 0 → 0 的空动画。
            element.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
            element.MaxHeight = double.PositiveInfinity;
            return;
        }

        // 被打断时从当前高度接着长，不要从 0 重来（否则连点会闪一下）。
        var from = double.IsPositiveInfinity(current) || current <= 0 ? 0 : Math.Min(current, target);

        var animation = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = MotionCore.EaseOut(),
            FillBehavior = FillBehavior.Stop,
        };

        animation.Completed += (_, _) =>
        {
            if (state.Token != token) return;
            element.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
            element.MaxHeight = double.PositiveInfinity;
        };

        element.BeginAnimation(FrameworkElement.MaxHeightProperty, animation);
    }

    /// <summary>
    /// 收起：MaxHeight 从当前高度动到 0，结束后 MaxHeight=0 且 Visibility=Collapsed。
    /// </summary>
    /// <param name="element">要收起的元素。</param>
    /// <param name="durationMs">时长（毫秒），&lt;=0 时回落到 180。</param>
    public static void Collapse(FrameworkElement element, int durationMs = DefaultDurationMs)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (durationMs <= 0) durationMs = DefaultDurationMs;

        var state = StateOf(element);
        var token = ++state.Token;

        if (element.Visibility != Visibility.Visible)
        {
            element.MaxHeight = 0;
            element.Visibility = Visibility.Collapsed;
            return;
        }

        element.ClipToBounds = true;

        var from = element.ActualHeight > 0 ? element.ActualHeight : MeasureHeight(element);
        if (from <= 0.5)
        {
            element.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
            element.MaxHeight = 0;
            element.Visibility = Visibility.Collapsed;
            return;
        }

        // 基值 = 终值 0；动画从当前高度降到 0，Stop 结束后回落到 0。
        element.MaxHeight = 0;

        var animation = new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = MotionCore.EaseOut(),
            FillBehavior = FillBehavior.Stop,
        };

        animation.Completed += (_, _) =>
        {
            if (state.Token != token) return;
            element.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
            element.MaxHeight = 0;
            element.Visibility = Visibility.Collapsed;
        };

        element.BeginAnimation(FrameworkElement.MaxHeightProperty, animation);
    }

    /// <summary>
    /// 量出元素在「不受高度限制」时的自然高度。元素可能还是 Collapsed / 尚未参与布局，
    /// 所以先按当前宽度测量一遍，宽度未知时退回父容器的宽度。
    /// 调用前必须已经把 MaxHeight 放开，否则量到的永远是限制值。
    /// </summary>
    private static double MeasureHeight(FrameworkElement element)
    {
        var width = element.ActualWidth;
        if (width <= 0 && element.Parent is FrameworkElement parent) width = parent.ActualWidth;
        if (width <= 0) width = double.PositiveInfinity;

        element.Measure(new Size(width, double.PositiveInfinity));
        return element.DesiredSize.Height;
    }

    private sealed class RevealState
    {
        /// <summary>最近一次动画的序号，用来让过期的 Completed 回调失效。</summary>
        public int Token { get; set; }
    }

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State",
        typeof(RevealState),
        typeof(RevealPanel),
        new PropertyMetadata(null));

    private static RevealState StateOf(DependencyObject element)
    {
        if (element.GetValue(StateProperty) is RevealState existing) return existing;

        var created = new RevealState();
        element.SetValue(StateProperty, created);
        return created;
    }
}
