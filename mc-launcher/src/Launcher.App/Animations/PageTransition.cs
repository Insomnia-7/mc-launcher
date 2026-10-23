using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Launcher.App.Animations;

/// <summary>
/// 切页动效原语：淡入 + 轻微上移归零（Opacity 0→1，TranslateY offsetY→0）。
/// <para>代码一行调用：<c>PageTransition.Play(this);</c></para>
/// <para>XAML 一行声明：<c>&lt;Grid anim:PageTransition.AutoPlay="True"&gt;</c>，元素首次 Loaded 或重新变可见时自动播放。</para>
/// <para>只管 <see cref="UIElement.RenderTransform"/>，不碰布局，所以不会引起相邻元素跳动。</para>
/// </summary>
public static class PageTransition
{
    private const int DefaultDurationMs = 200;
    private const double DefaultOffsetY = 10;

    /// <summary>
    /// 自动播放开关（附加属性）。设为 true 后：元素第一次 Loaded、或 Visibility 从 Collapsed/Hidden 变回
    /// Visible 时自动播放一次切入动画；重复切换只会重播，不会叠加。
    /// </summary>
    public static readonly DependencyProperty AutoPlayProperty = DependencyProperty.RegisterAttached(
        "AutoPlay",
        typeof(bool),
        typeof(PageTransition),
        new PropertyMetadata(false, OnAutoPlayChanged));

    /// <summary>读取 <see cref="AutoPlayProperty"/>。</summary>
    public static bool GetAutoPlay(DependencyObject element) => (bool)element.GetValue(AutoPlayProperty);

    /// <summary>写入 <see cref="AutoPlayProperty"/>。</summary>
    public static void SetAutoPlay(DependencyObject element, bool value) => element.SetValue(AutoPlayProperty, value);

    /// <summary>
    /// 播放一次切入动画：透明度 0→1、Y 位移 <paramref name="offsetY"/>→0，CubicEase EaseOut。
    /// </summary>
    /// <param name="element">目标元素（通常是页面根容器）。</param>
    /// <param name="offsetY">起始下移像素，0 表示只淡入。</param>
    /// <param name="durationMs">时长（毫秒），&lt;=0 时回落到 200。</param>
    /// <remarks>
    /// 重复调用是安全的：<see cref="UIElement.BeginAnimation(DependencyProperty, AnimationTimeline)"/> 会顶掉上一段动画，
    /// 不会叠加，也不会从上一段的中间值起跳（每次都是 0 → 1 的完整切入）。
    /// </remarks>
    public static void Play(FrameworkElement element, double offsetY = DefaultOffsetY, int durationMs = DefaultDurationMs)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (!MotionPreferences.Enabled) return;
        if (durationMs <= 0) durationMs = DefaultDurationMs;

        var state = MotionCore.Ensure(element);
        var duration = TimeSpan.FromMilliseconds(durationMs);

        // 基值先写成「终点值」：FillBehavior.Stop 的动画一结束就会回落到基值，天然不留中间值。
        element.Opacity = 1;
        state.Translate.Y = 0;

        var token = ++state.Token;

        var fade = new DoubleAnimation(0, 1, duration)
        {
            EasingFunction = MotionCore.EaseOut(),
            FillBehavior = FillBehavior.Stop,
        };
        var slide = new DoubleAnimation(offsetY, 0, duration)
        {
            EasingFunction = MotionCore.EaseOut(),
            FillBehavior = FillBehavior.Stop,
        };

        element.BeginAnimation(UIElement.OpacityProperty, fade);

        // Completed 必须在 BeginAnimation 之前挂：动画时钟在 BeginAnimation 时创建，
        // 之后再挂的事件收不到（表现为「播完了但回调不执行」，动画时钟一直挂在属性上）。
        slide.Completed += (_, _) =>
        {
            // 期间又被 Play 过一次：收尾交给新的那段，别把新动画清掉。
            if (state.Token != token) return;

            state.Translate.BeginAnimation(TranslateTransform.YProperty, null);
            state.Translate.Y = 0;
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
        };

        state.Translate.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    private static void OnAutoPlayChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;

        if (e.NewValue is true)
        {
            element.Loaded -= OnAutoPlayLoaded;
            element.Loaded += OnAutoPlayLoaded;
            element.IsVisibleChanged -= OnAutoPlayVisibilityChanged;
            element.IsVisibleChanged += OnAutoPlayVisibilityChanged;

            // 属性是在元素已经显示之后才设的：补一次，别等下一次可见性变化。
            if (element.IsLoaded && element.IsVisible) PlayWhenSettled(element);
        }
        else
        {
            element.Loaded -= OnAutoPlayLoaded;
            element.IsVisibleChanged -= OnAutoPlayVisibilityChanged;
        }
    }

    private static void OnAutoPlayLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;

        // 「第一次 Loaded」才播：来回切换页面时 Loaded 会重复触发，重播交给可见性那条路。
        if (element.GetValue(PlayedOnLoadProperty) is true) return;
        element.SetValue(PlayedOnLoadProperty, true);

        if (element.IsVisible) PlayWhenSettled(element);
    }

    private static void OnAutoPlayVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        if (e.NewValue is not true) return;

        PlayWhenSettled(element);
    }

    /// <summary>
    /// 延后一帧再播：Visibility 刚变 Visible 时布局还没跑完，此刻 Play 会从「还没有尺寸」的状态起步，
    /// 第一帧容易看见跳变。
    /// </summary>
    private static void PlayWhenSettled(FrameworkElement element)
    {
        element.Dispatcher.BeginInvoke(
            DispatcherPriority.Render,
            new Action(() =>
            {
                if (element.IsVisible) Play(element);
            }));
    }

    private static readonly DependencyProperty PlayedOnLoadProperty = DependencyProperty.RegisterAttached(
        "PlayedOnLoad",
        typeof(bool),
        typeof(PageTransition),
        new PropertyMetadata(false));
}

/// <summary>
/// 每个元素一份的变换状态：我们只往 <see cref="TransformGroup"/> 里塞自己的
/// <see cref="ScaleTransform"/> 与 <see cref="TranslateTransform"/>，模板原有的变换原样保留。
/// </summary>
internal sealed class MotionState
{
    /// <summary>缩放（按压反馈用）。</summary>
    public ScaleTransform Scale { get; } = new(1, 1);

    /// <summary>位移（切页 / 悬停上移用）。</summary>
    public TranslateTransform Translate { get; } = new();

    /// <summary>挂在元素上的变换组。</summary>
    public TransformGroup Group { get; } = new();

    /// <summary>最近一次动画的序号，用来判断 Completed 回调是否已经过期。</summary>
    public int Token { get; set; }
}

/// <summary>
/// 三个动效原语共用的底层：装变换、给缓动。
/// </summary>
internal static class MotionCore
{
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State",
        typeof(MotionState),
        typeof(MotionCore),
        new PropertyMetadata(null));

    /// <summary>拿到（必要时创建）元素上的 <see cref="MotionState"/>。</summary>
    /// <remarks>
    /// RenderTransform 可能已被控件模板占用（旋转、翻转、模板自带的缩放）。这里的策略：
    /// 没有就新建；已经是我们的 TransformGroup 就直接复用；其它类型（含模板给的单变换）
    /// 原样塞进新组的第 0 位，我们的 Scale / Translate 追加在后面，互不覆盖。
    /// 冻结的变换不能直接进可变组，先 <see cref="Freezable.CloneCurrentValue"/> 解冻。
    /// </remarks>
    public static MotionState Ensure(FrameworkElement element)
    {
        if (element.GetValue(StateProperty) is MotionState existing) return existing;

        var state = new MotionState();

        var previous = element.RenderTransform;
        if (previous is not null && !ReferenceEquals(previous, Transform.Identity))
        {
            state.Group.Children.Add(previous.IsFrozen ? previous.CloneCurrentValue() : previous);
        }

        state.Group.Children.Add(state.Scale);
        state.Group.Children.Add(state.Translate);

        element.RenderTransform = state.Group;

        // 缩放必须以元素中心为原点，否则按下去会往右下角缩（原点默认在左上角）。
        // 位移不受原点影响，所以这一句对切页动画无害。
        element.RenderTransformOrigin = new Point(0.5, 0.5);

        element.SetValue(StateProperty, state);
        return state;
    }

    /// <summary>读回元素上已有的 <see cref="MotionState"/>；没装过变换时返回 null（不创建）。</summary>
    public static MotionState? TryGet(FrameworkElement element) => element.GetValue(StateProperty) as MotionState;

    /// <summary>统一的缓动曲线：CubicEase EaseOut（快进慢出，收尾最像「落位」）。</summary>
    /// <remarks>每次新建实例：EasingFunction 是 Freezable，多个动画共用一个实例容易踩冻结/共享的坑。</remarks>
    public static CubicEase EaseOut() => new() { EasingMode = EasingMode.EaseOut };
}
