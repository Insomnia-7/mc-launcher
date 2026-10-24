using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Launcher.App.Animations;

/// <summary>Keep the outgoing page visible through its fade; stage entry after layout has settled.</summary>
public static class PageVisibility
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
        "IsActive", typeof(bool), typeof(PageVisibility), new PropertyMetadata(false, OnActive));
    public static bool GetIsActive(DependencyObject value) => (bool)value.GetValue(IsActiveProperty);
    public static void SetIsActive(DependencyObject value, bool active) => value.SetValue(IsActiveProperty, active);

    public static readonly DependencyProperty DialogProperty = DependencyProperty.RegisterAttached(
        "Dialog", typeof(bool), typeof(PageVisibility), new PropertyMetadata(false));
    public static bool GetDialog(DependencyObject value) => (bool)value.GetValue(DialogProperty);
    public static void SetDialog(DependencyObject value, bool dialog) => value.SetValue(DialogProperty, dialog);

    private static FrameworkElement Surface(FrameworkElement root)
    {
        if (!GetDialog(root)) return root;
        return root.FindName("DialogSurface") as FrameworkElement
            ?? root.FindName("ConfirmSurface") as FrameworkElement
            ?? root;
    }

    private static void OnActive(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not FrameworkElement root) return;
        var active = (bool)args.NewValue;
        var state = MotionCore.Ensure(root);
        var token = ++state.Token;
        root.IsHitTestVisible = active;
        var surface = Surface(root);
        var content = MotionCore.Ensure(surface);
        var dialog = GetDialog(root);

        if (!MotionPreferences.Enabled)
        {
            Reset(root, content);
            root.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        if (active)
        {
            root.Visibility = Visibility.Visible;
            root.BeginAnimation(UIElement.OpacityProperty, null);
            root.Opacity = 0;
            content.Translate.BeginAnimation(TranslateTransform.YProperty, null);
            content.Translate.Y = dialog ? 18 : 24;
            content.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            content.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            content.Scale.ScaleX = content.Scale.ScaleY = dialog ? .975 : 1;

            // 入场动画也靠渲染帧推进：窗口最小化 / 远程桌面断帧时它会停在 opacity=0，
            // 那一页就彻底看不见了。到点还没露出来就直接摆到终态。
            ArmFallback(state, content, token, root, active: true, milliseconds: 900);

            // Collapsed pages may not yet have been loaded or arranged. Never skip their animation.
            // Render（7）而不是 Loaded（6）：Loaded 优先级低于 Normal，在持续有 Normal 级工作的进程里
            // （例如自动化检查的忙轮询）会被饿死，弹窗就一直停在 opacity=0 的初态；Render 每帧都会轮到，
            // 而且仍在布局之后执行，语义上依旧满足「布局稳定后再入场」。
            root.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                if (state.Token != token || !GetIsActive(root)) return;
                if (!MotionPreferences.Enabled) { Reset(root, content); return; }
                root.UpdateLayout();
                root.Opacity = 1;
                content.Translate.Y = 0;
                content.Scale.ScaleX = content.Scale.ScaleY = 1;
                root.BeginAnimation(UIElement.OpacityProperty, Tween(0, 1, 280));
                var slide = Tween(dialog ? 18 : 24, 0, 340);
                slide.Completed += (_, _) => { if (state.Token == token) Reset(root, content); };
                content.Translate.BeginAnimation(TranslateTransform.YProperty, slide);
                if (dialog)
                {
                    content.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, Tween(.975, 1, 340));
                    content.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, Tween(.975, 1, 340));
                }
            }));
            return;
        }

        var opacity = root.Opacity;
        var y = content.Translate.Y;
        root.Opacity = 0;
        var fade = Tween(opacity, 0, 180);
        fade.Completed += (_, _) =>
        {
            if (state.Token != token || GetIsActive(root)) return;
            root.Visibility = Visibility.Collapsed;
            Reset(root, content);
        };
        root.BeginAnimation(UIElement.OpacityProperty, fade);
        content.Translate.Y = 0;
        content.Translate.BeginAnimation(TranslateTransform.YProperty, Tween(y, dialog ? 10 : -12, 180));
        // 淡出同理：帧停了页面就停在「可见但透明」，再点回来会以为界面没了。
        ArmFallback(state, content, token, root, active: false, milliseconds: 400);
    }

    /// <summary>
    /// 动画的兜底：到点还没走到终态就直接摆正。
    /// 渲染帧只在有动画/有变化时才推进，窗口最小化、远程会话断帧、离屏截图都会让它停住，
    /// 停在中间态的结果是「页面透明但占位」或「页面永远不出现」，比没有动画糟得多。
    /// </summary>
    private static void ArmFallback(MotionState state, MotionState content, long token, FrameworkElement root, bool active, int milliseconds)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (state.Token != token) return;      // 已被更新的切换取代，交给那一轮收尾
            if (!MotionPreferences.Enabled) { Reset(root, content); return; }
            if (active)
            {
                if (GetIsActive(root) && root.Opacity < 0.99) Reset(root, content);
            }
            else if (!GetIsActive(root))
            {
                root.Visibility = Visibility.Collapsed;
                Reset(root, content);
            }
        };
        timer.Start();
    }

    private static DoubleAnimation Tween(double from, double to, int milliseconds) => new(from, to, TimeSpan.FromMilliseconds(milliseconds))
    {
        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        FillBehavior = FillBehavior.Stop
    };

    private static void Reset(FrameworkElement root, MotionState content)
    {
        root.BeginAnimation(UIElement.OpacityProperty, null);
        root.Opacity = 1;
        content.Translate.BeginAnimation(TranslateTransform.YProperty, null);
        content.Translate.Y = 0;
        content.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        content.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        content.Scale.ScaleX = content.Scale.ScaleY = 1;
    }
}
