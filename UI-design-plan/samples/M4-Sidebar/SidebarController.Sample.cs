using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace Launcher.Desktop.Shell;

/// <summary>
/// 遵循 UI.md §6, §15, §23, §27：
/// - Expanded = 240px, Collapsed = 72px（总过渡时长 250ms）
/// - 展开时先扩宽（t=0 启动 250ms），延迟 80ms 后文字淡入 + 水平微移归零（140ms）
/// - 收起时先隐藏文字（120ms 淡出 + -6px 位移），同步收缩宽度（250ms），避免文字硬裁切
/// - 选中提示条单一连续平滑移动（190ms），支持快速连点中断恢复
/// </summary>
public sealed class SidebarController : IDisposable
{
    public const double ExpandedWidth = 240d;
    public const double CollapsedWidth = 72d;

    private readonly Border _sidebarPanel;
    private readonly StackPanel _navList;
    private readonly Border _indicator;
    private CancellationTokenSource? _toggleCts;
    private CancellationTokenSource? _indicatorCts;

    public SidebarController(Border sidebarPanel, StackPanel navList, Border indicator)
    {
        _sidebarPanel = sidebarPanel;
        _navList = navList;
        _indicator = indicator;
    }

    public async Task TransitionExpandedStateAsync(bool expanded, bool userInitiated)
    {
        _toggleCts?.Cancel();
        _toggleCts?.Dispose();
        var cts = _toggleCts = new CancellationTokenSource();
        var token = cts.Token;

        var labels = _sidebarPanel.GetVisualDescendants()
            .OfType<Control>()
            .Where(c => c.Classes.Contains("sidebar-label") || c.Name == "SidebarBrandTextHost")
            .ToArray();

        foreach (var navBtn in _navList.Children.OfType<Button>())
            ToolTip.SetIsEnabled(navBtn, !expanded);

        if (!userInitiated || !MotionPreferences.Enabled)
        {
            ApplyStaticState(expanded, labels);
            return;
        }

        try
        {
            if (!expanded)
            {
                // 收起：先淡出文字并向左微移 -6px（120ms），同时容器宽度向 72px 收缩（250ms）
                var fadeTasks = labels.Select(label => Task.WhenAll(
                    Motion.AnimateOpacity(label, label.Opacity, 0, 120, MotionEasing.Exit, token),
                    Motion.AnimateTranslateX(label, 0, -6, 120, MotionEasing.Exit, token)));

                _sidebarPanel.Width = CollapsedWidth;
                await Task.WhenAll(fadeTasks);

                foreach (var label in labels) label.IsVisible = false;
                _sidebarPanel.Classes.Set("nav-compact", true);
            }
            else
            {
                // 展开：先准备布局并启动宽度扩张（72 -> 240），延迟 80ms 后文字淡入归位（140ms）
                _sidebarPanel.Classes.Set("nav-compact", false);
                foreach (var label in labels)
                {
                    label.Opacity = 0;
                    label.IsVisible = true;
                }

                _sidebarPanel.Width = ExpandedWidth;
                await Task.Delay(80, token);

                var enterTasks = labels.Select(label => Task.WhenAll(
                    Motion.AnimateOpacity(label, 0, 1, 140, MotionEasing.Entrance, token),
                    Motion.AnimateTranslateX(label, -6, 0, 140, MotionEasing.Entrance, token)));

                await Task.WhenAll(enterTasks);
            }
        }
        catch (OperationCanceledException)
        {
            // 被用户快速二次点击打断时，由新任务接管过渡
        }
    }

    private void ApplyStaticState(bool expanded, IReadOnlyList<Control> labels)
    {
        _sidebarPanel.Classes.Set("nav-compact", !expanded);
        _sidebarPanel.Width = expanded ? ExpandedWidth : CollapsedWidth;
        foreach (var label in labels)
        {
            label.IsVisible = expanded;
            label.Opacity = expanded ? 1 : 0;
            if (label.RenderTransform is TranslateTransform tt) tt.X = 0;
        }
    }

    public void MoveIndicatorTo(Button? activeButton, bool animate)
    {
        if (activeButton is null || !activeButton.IsAttachedToVisualTree())
        {
            _indicator.IsVisible = false;
            return;
        }

        var origin = activeButton.TranslatePoint(
            new Point(0, (activeButton.Bounds.Height - _indicator.Height) / 2),
            _navList);

        if (origin is not { } targetPoint || _indicator.RenderTransform is not TranslateTransform transform)
            return;

        _indicator.IsVisible = true;
        var targetX = -6d;
        var targetY = targetPoint.Y;

        if (!animate || !MotionPreferences.Enabled)
        {
            transform.X = targetX;
            transform.Y = targetY;
            return;
        }

        _indicatorCts?.Cancel();
        _indicatorCts?.Dispose();
        var cts = _indicatorCts = new CancellationTokenSource();
        _ = RunIndicatorAnimationAsync(transform, transform.Y, targetY, cts.Token);
    }

    private async Task RunIndicatorAnimationAsync(TranslateTransform transform, double fromY, double toY, CancellationToken token)
    {
        try
        {
            var anim = new Animation
            {
                Duration = TimeSpan.FromMilliseconds(190),
                Easing = MotionEasing.Entrance,
                FillMode = FillMode.Forward
            };
            anim.Children.Add(new KeyFrame { Cue = new Cue(0), Setters = { new Setter(TranslateTransform.YProperty, fromY) } });
            anim.Children.Add(new KeyFrame { Cue = new Cue(1), Setters = { new Setter(TranslateTransform.YProperty, toY) } });
            await anim.RunAsync(_indicator, token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (!token.IsCancellationRequested)
                transform.Y = toY;
        }
    }

    public void Dispose()
    {
        _toggleCts?.Cancel();
        _toggleCts?.Dispose();
        _indicatorCts?.Cancel();
        _indicatorCts?.Dispose();
    }
}
