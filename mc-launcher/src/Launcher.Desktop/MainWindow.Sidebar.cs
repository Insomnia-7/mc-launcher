using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Launcher.App.ViewModels;
using Launcher.Desktop.Controls;

namespace Launcher.Desktop;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _sidebarCts;
    private int _navIndicatorRevision;
    private bool _navIndicatorPlaced;
    private bool _navIndicatorAnimated;
    private Rect? _navBounds;

    private IEnumerable<Control> SidebarLabels()
    {
        yield return SidebarNavText;
        yield return SidebarBottomText;
        foreach (var text in NavList.GetVisualDescendants().OfType<IconLabel>()
                     .SelectMany(label => label.GetVisualDescendants().OfType<TextBlock>()))
            yield return text;
    }

    private void SetSidebarLabels(bool expanded, int duration = 0)
    {
        foreach (var label in SidebarLabels())
        {
            if (duration > 0)
            {
                if (label.Transitions?.FirstOrDefault() is DoubleTransition fade)
                    fade.Duration = TimeSpan.FromMilliseconds(duration);
                else label.Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(duration), Easing = MotionEasing.Linear }];
            }
            else label.Transitions = null;
            if (label.RenderTransform is not TranslateTransform offset)
                label.RenderTransform = offset = new TranslateTransform();
            if (duration > 0)
            {
                if (offset.Transitions?.FirstOrDefault() is DoubleTransition shift)
                    shift.Duration = TimeSpan.FromMilliseconds(duration);
                else offset.Transitions = [new DoubleTransition { Property = TranslateTransform.XProperty, Duration = TimeSpan.FromMilliseconds(duration), Easing = MotionEasing.Entrance }];
            }
            else offset.Transitions = null;
            label.Opacity = expanded ? 1 : 0;
            offset.X = expanded ? 0 : -6;
        }
    }

    private void ApplySidebarLayout(SidebarState state)
    {
        var expanded = state is SidebarState.Expanded or SidebarState.Overlay;
        SidebarPanel.Classes.Set("nav-compact", state == SidebarState.Compact);
        SidebarBrandExpanded.IsVisible = expanded;
        SidebarBrandCollapsed.IsVisible = state == SidebarState.Compact;
        SidebarBottomCompact.IsVisible = state == SidebarState.Compact;
        SidebarBottomText.IsVisible = expanded;
        foreach (var button in NavList.GetVisualDescendants().OfType<Button>())
            ToolTip.SetServiceEnabled(button, !expanded);
    }

    private void UpdateSidebar(bool userInitiated)
    {
        var width = Bounds.Width > 0 ? Bounds.Width : ClientSize.Width;
        var state = width is > 0 and < 900
            ? (_overlaySidebarOpen ? SidebarState.Overlay : SidebarState.Minimal)
            : (_model?.IsSidebarCollapsed == true || width is >= 900 and < 1000 ? SidebarState.Compact : SidebarState.Expanded);
        var previous = _sidebarState;
        _sidebarState = state;
        _sidebarCts?.Cancel();
        _sidebarCts?.Dispose();
        _sidebarCts = null;
        var animated = userInitiated && previous != state && MotionPreferences.Enabled;
        var fold = animated && (previous is SidebarState.Expanded or SidebarState.Compact) && (state is SidebarState.Expanded or SidebarState.Compact);
        // Keep the running transition so a second toggle reverses from the visible width.
        if (!fold) SidebarPanel.Transitions = null;
        else if (SidebarPanel.Transitions?.FirstOrDefault() is not DoubleTransition)
            SidebarPanel.Transitions = [new DoubleTransition { Property = WidthProperty, Duration = MotionTokens.Sidebar, Easing = MotionEasing.Entrance }];
        SidebarPanel.Classes.Set("nav-minimal", state == SidebarState.Minimal);
        SidebarPanel.Classes.Set("nav-overlay", state == SidebarState.Overlay);
        Grid.SetColumnSpan(SidebarPanel, state == SidebarState.Overlay ? 2 : 1);
        SidebarPanel.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        SidebarPanel.Width = state switch { SidebarState.Expanded or SidebarState.Overlay => 216, SidebarState.Compact => 64, _ => 0 };
        // Explicitly release the expanded column after a native window resize. An Auto column
        // can retain the wider measure while a transition or native restore is in flight.
        UpdateSidebarColumn();
        SidebarScrim.IsVisible = state == SidebarState.Overlay;
        SidebarHamburger.IsVisible = state == SidebarState.Minimal;
        PageContent.Classes.Set("minimal", state == SidebarState.Minimal);
        SidebarPanel.RenderTransform = null;
        if (fold)
        {
            var cts = _sidebarCts = new CancellationTokenSource();
            _ = FoldSidebarAsync(state, cts.Token);
        }
        else
        {
            ApplySidebarLayout(state);
            SetSidebarLabels(state is SidebarState.Expanded or SidebarState.Overlay);
            UpdateNavIndicator(animate: false);
            if (animated && state == SidebarState.Overlay)
            {
                var cts = _sidebarCts = new CancellationTokenSource();
                _ = ShowOverlaySidebarAsync(cts.Token);
            }
        }
    }

    private void UpdateSidebarColumn() => Shell.ColumnDefinitions[0].Width = new GridLength(
        _sidebarState is SidebarState.Expanded or SidebarState.Compact ? Math.Max(0, SidebarPanel.Width) : 0);

    private async Task FoldSidebarAsync(SidebarState state, CancellationToken token)
    {
        try
        {
            if (state == SidebarState.Compact)
            {
                SetSidebarLabels(false, MotionTiming.SidebarTextOutMs);
                await Task.Delay(MotionTiming.SidebarTextOutMs, token);
                ApplySidebarLayout(state);
                await Task.Delay((int)MotionTokens.Sidebar.TotalMilliseconds - MotionTiming.SidebarTextOutMs, token);
            }
            else
            {
                ApplySidebarLayout(state);
                await Task.Delay(MotionTiming.SidebarTextDelayMs, token);
                SetSidebarLabels(true, MotionTiming.SidebarTextInMs);
                await Task.Delay((int)MotionTokens.Sidebar.TotalMilliseconds - MotionTiming.SidebarTextDelayMs, token);
            }
            token.ThrowIfCancellationRequested();
            SetSidebarLabels(state == SidebarState.Expanded);
        }
        catch (OperationCanceledException) { }
    }

    private async Task ShowOverlaySidebarAsync(CancellationToken token)
    {
        try { await Motion.AnimateTranslateX(SidebarPanel, -24, 0, MotionTiming.PanelMs, MotionEasing.Entrance, token); }
        catch (OperationCanceledException) { }
        finally { if (!token.IsCancellationRequested) SidebarPanel.RenderTransform = null; }
    }

    private void OpenOverlaySidebar(object? sender, RoutedEventArgs e)
    {
        _overlaySidebarOpen = true;
        UpdateSidebar(userInitiated: true);
    }

    private void OnScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        _overlaySidebarOpen = false;
        UpdateSidebar(userInitiated: false);
    }

    private string? NavButtonName() => _model?.ShellPage switch
    {
        "home" => "HomeNav",
        "showcase" => "ShowcaseNav",
        "library" or "detail" or "horizon" => "LibraryNav",
        "discover" => "DiscoverNav",
        "multiplayer" => "MultiplayerNav",
        "network" => "NetworkNav",
        "settings" => "SettingsEntry",
        "experience" => _model.IsRolePage ? null : _model.IsTaskPage ? "TaskCenterEntry" : null,
        _ => null
    };

    private void UpdateNavIndicator(bool animate = true)
    {
        if (NavButtonName() is not { } name || this.FindControl<Button>(name) is not { } button || !button.IsEffectivelyVisible
            || button.TranslatePoint(default, NavList) is not { } origin)
        {
            NavIndicator.IsVisible = NavSelection.IsVisible = false;
            return;
        }
        var animated = animate && MotionPreferences.Enabled && _navIndicatorPlaced;
        var indicator = (TranslateTransform)NavIndicator.RenderTransform!;
        var selection = (TranslateTransform)NavSelection.RenderTransform!;
        if (animated != _navIndicatorAnimated)
        {
            foreach (var transform in new[] { indicator, selection })
                transform.Transitions = animated
                    ? [new DoubleTransition { Property = TranslateTransform.XProperty, Duration = MotionTiming.Navigation, Easing = MotionEasing.Entrance },
                       new DoubleTransition { Property = TranslateTransform.YProperty, Duration = MotionTiming.Navigation, Easing = MotionEasing.Entrance }]
                    : null;
            _navIndicatorAnimated = animated;
        }
        var bounds = new Rect(origin, button.Bounds.Size);
        // Expanding the sidebar changes width without changing the button's origin.
        if (_navBounds != bounds || !animated)
        {
            indicator.X = origin.X + button.Bounds.Width - 3;
            indicator.Y = origin.Y + (button.Bounds.Height - NavIndicator.Height) / 2;
            selection.X = origin.X;
            selection.Y = origin.Y;
            _navBounds = bounds;
        }
        NavSelection.Width = button.Bounds.Width;
        NavSelection.Height = button.Bounds.Height;
        NavIndicator.IsVisible = NavSelection.IsVisible = true;
        _navIndicatorPlaced = true;
    }

    private void NavListLayoutUpdated() => NavList.LayoutUpdated += (_, _) => UpdateNavIndicator();
}
