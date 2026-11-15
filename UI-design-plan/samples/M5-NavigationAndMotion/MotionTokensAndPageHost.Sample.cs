using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Styling;

namespace Launcher.Desktop;

/// <summary>
/// 遵循 UI.md §27「动画系统实现」的统一 Motion Tokens。
/// 全项目所有动画时长必须引用此类，严禁在各 View 私自散落裸毫秒数。
/// </summary>
public static class MotionTokens
{
    public static TimeSpan Hover => TimeSpan.FromMilliseconds(120);           // 规范 100-140ms
    public static TimeSpan Pressed => TimeSpan.FromMilliseconds(85);          // 规范 70-100ms
    public static TimeSpan Release => TimeSpan.FromMilliseconds(140);         // 规范 120-160ms
    public static TimeSpan NavIndicator => TimeSpan.FromMilliseconds(190);    // 规范 160-220ms
    public static TimeSpan Sidebar => TimeSpan.FromMilliseconds(250);         // 规范 220-280ms
    public static TimeSpan TextFade => TimeSpan.FromMilliseconds(140);        // 规范 120-160ms
    public static TimeSpan PageSwitch => TimeSpan.FromMilliseconds(200);      // 规范 180-240ms
    public static TimeSpan PageExit => TimeSpan.FromMilliseconds(110);        // 旧页面快速退场
    public static TimeSpan ContentEntrance => TimeSpan.FromMilliseconds(220); // 规范 180-260ms
    public static TimeSpan Dialog => TimeSpan.FromMilliseconds(220);          // 规范 180-260ms
    public static TimeSpan Tooltip => TimeSpan.FromMilliseconds(140);         // 规范 120-160ms

    public const int StaggerDelayMs = 40;                                     // 规范 35-55ms
    public const int TooltipShowDelayMs = 450;                                // 规范 400-500ms
}

/// <summary>
/// 遵循 UI.md §15 & §24 的双面板页面切换宿主：
/// - 旧页面：Opacity 1 -> 0，Y 轴 0 -> -4px
/// - 新页面：Opacity 0 -> 1，Y 轴 8px -> 0
/// - 底层挂载实色 BgBrush 托底，杜绝交叉半透明期间闪白/闪黑
/// - 支持快速连续点击导航即时中断上一段过渡并安全收敛状态
/// </summary>
public sealed class AnimatedContentHost : Grid
{
    private readonly ContentPresenter _outgoing = new() { IsVisible = false, IsHitTestVisible = false };
    private readonly ContentPresenter _incoming = new();
    private CancellationTokenSource? _transitionCts;
    private int _revision;

    public static readonly StyledProperty<Control?> PageContentProperty =
        AvaloniaProperty.Register<AnimatedContentHost, Control?>(nameof(PageContent));

    public Control? PageContent
    {
        get => GetValue(PageContentProperty);
        set => SetValue(PageContentProperty, value);
    }

    public AnimatedContentHost()
    {
        ClipToBounds = true;
        _outgoing.RenderTransform = new TranslateTransform(0, 0);
        _incoming.RenderTransform = new TranslateTransform(0, 0);
        Children.Add(_outgoing);
        Children.Add(_incoming);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PageContentProperty)
            SwitchPage(change.GetOldValue<Control?>(), change.GetNewValue<Control?>());
    }

    private async void SwitchPage(Control? previous, Control? next)
    {
        if (ReferenceEquals(previous, next)) return;

        _transitionCts?.Cancel();
        _transitionCts?.Dispose();
        _transitionCts = null;
        var currentRevision = ++_revision;

        if (previous is null || next is null || !MotionPreferences.Enabled)
        {
            _outgoing.Content = null;
            _outgoing.IsVisible = false;
            ResetVisual(_incoming, next);
            return;
        }

        var cts = _transitionCts = new CancellationTokenSource();
        var token = cts.Token;

        _outgoing.Content = null;
        _incoming.Content = null;

        _outgoing.Content = previous;
        _outgoing.Opacity = 1;
        _outgoing.IsVisible = true;
        SetTranslateY(_outgoing, 0);

        _incoming.Content = next;
        _incoming.Opacity = 0;
        _incoming.IsVisible = true;
        SetTranslateY(_incoming, 8);

        try
        {
            var exitTask = RunTransitionAsync(_outgoing, 1, 0, 0, -4, MotionTokens.PageExit, MotionEasing.Exit, token);
            var enterTask = RunTransitionAsync(_incoming, 0, 1, 8, 0, MotionTokens.PageSwitch, MotionEasing.Entrance, token);
            await Task.WhenAll(exitTask, enterTask);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (currentRevision == _revision)
            {
                _outgoing.Content = null;
                _outgoing.IsVisible = false;
                ResetVisual(_incoming, next);
            }
        }
    }

    private static void ResetVisual(ContentPresenter presenter, Control? content)
    {
        presenter.Content = content;
        presenter.Opacity = 1;
        presenter.IsVisible = content is not null;
        SetTranslateY(presenter, 0);
    }

    private static void SetTranslateY(Control target, double y)
    {
        if (target.RenderTransform is TranslateTransform tt) tt.Y = y;
        else target.RenderTransform = new TranslateTransform(0, y);
    }

    private static Task RunTransitionAsync(
        Control target,
        double fromOpacity,
        double toOpacity,
        double fromY,
        double toY,
        TimeSpan duration,
        Easing easing,
        CancellationToken token)
    {
        var anim = new Animation
        {
            Duration = duration,
            Easing = easing,
            FillMode = FillMode.Forward
        };
        anim.Children.Add(new KeyFrame
        {
            Cue = new Cue(0),
            Setters =
            {
                new Setter(Visual.OpacityProperty, fromOpacity),
                new Setter(TranslateTransform.YProperty, fromY)
            }
        });
        anim.Children.Add(new KeyFrame
        {
            Cue = new Cue(1),
            Setters =
            {
                new Setter(Visual.OpacityProperty, toOpacity),
                new Setter(TranslateTransform.YProperty, toY)
            }
        });
        return anim.RunAsync(target, token);
    }

    /// <summary>
    /// 遵循 UI.md §15 内容交错入场（顺序：横幅 -> 快速启动 -> 联机状态 -> 游戏实例 -> 快捷功能 -> 辅助信息）。
    /// 减少动画开启时直接瞬时显示，且全程不禁用 HitTest、不阻塞用户立即点击启动按钮。
    /// </summary>
    public static async Task PlayHomeStaggerAsync(IReadOnlyList<Control> orderedModules, CancellationToken token)
    {
        if (!MotionPreferences.Enabled)
        {
            foreach (var module in orderedModules)
            {
                module.Opacity = 1;
                SetTranslateY(module, 0);
            }
            return;
        }

        foreach (var module in orderedModules)
        {
            module.Opacity = 0;
            SetTranslateY(module, 8);
        }

        var tasks = new List<Task>(orderedModules.Count);
        for (var i = 0; i < orderedModules.Count; i++)
            tasks.Add(RunSingleStaggerAsync(orderedModules[i], i * MotionTokens.StaggerDelayMs, token));

        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException)
        {
            foreach (var module in orderedModules)
            {
                module.Opacity = 1;
                SetTranslateY(module, 0);
            }
        }
    }

    private static async Task RunSingleStaggerAsync(Control module, int delayMs, CancellationToken token)
    {
        if (delayMs > 0) await Task.Delay(delayMs, token);
        await RunTransitionAsync(module, 0, 1, 8, 0, MotionTokens.ContentEntrance, MotionEasing.Entrance, token);
        module.Opacity = 1;
        SetTranslateY(module, 0);
    }
}
