using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Threading;

namespace Launcher.Desktop.Controls;

/// <summary>Adjacent opaque pages slide vertically; no cross-fade or text overlays.</summary>
public sealed class AnimatedContentHost : Grid
{
    public static readonly StyledProperty<Control?> ContentProperty =
        AvaloniaProperty.Register<AnimatedContentHost, Control?>(nameof(Content));
    private readonly ContentPresenter _outgoing = new() { IsVisible = false, IsHitTestVisible = false, ClipToBounds = true };
    private readonly ContentPresenter _incoming = new() { ClipToBounds = true };
    private CancellationTokenSource? _switchCts;
    private int _switchVersion;
    private bool _attached;
    private bool _moving;
    public int Direction { get; set; } = 1;

    public Control? Content { get => GetValue(ContentProperty); set => SetValue(ContentProperty, value); }

    public AnimatedContentHost()
    {
        ClipToBounds = true;
        Children.Add(_outgoing);
        Children.Add(_incoming);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ContentProperty) SwitchPage(change.GetOldValue<Control?>(), change.GetNewValue<Control?>());
        else if (change.Property == BoundsProperty && _moving) FinishTransition();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        MotionPreferences.Changed += MotionChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        MotionPreferences.Changed -= MotionChanged;
        FinishTransition();
        base.OnDetachedFromVisualTree(e);
    }

    private void MotionChanged()
    {
        if (!MotionPreferences.Enabled) FinishTransition();
    }

    public void FinishTransition()
    {
        ++_switchVersion;
        _moving = false;
        _switchCts?.Cancel();
        _switchCts?.Dispose();
        _switchCts = null;
        Clear(_outgoing);
        Normalize(_incoming);
    }

    private static void Clear(ContentPresenter presenter)
    {
        Normalize(presenter);
        presenter.Content = null;
        // Release both logical and visual ownership before a cached page is reused in the other layer.
        presenter.UpdateChild();
        presenter.IsVisible = false;
    }

    private static void Normalize(Control presenter)
    {
        presenter.Transitions = null;
        presenter.Opacity = 1;
        presenter.RenderTransform = new TranslateTransform();
    }

    private void SwitchPage(Control? previous, Control? next)
    {
        FinishTransition();
        Clear(_incoming);
        var animate = _attached && MotionPreferences.Enabled && previous is not null && next is not null;
        _incoming.Background = Background;
        _outgoing.Background = Background;
        if (animate)
        {
            _outgoing.Content = previous;
            _outgoing.IsVisible = true;
            _outgoing.UpdateChild();
        }
        _incoming.Content = next;
        _incoming.IsVisible = next is not null;
        _incoming.UpdateChild();
        if (!animate) return;
        _switchCts = new CancellationTokenSource();
        var token = _switchCts.Token;
        var version = _switchVersion;
        // Wait for the new page margin/layout before measuring a full page of travel.
        // Until then the old opaque layer fills the viewport and the new page is outside it.
        var direction = Direction < 0 ? -1 : 1;
        _incoming.RenderTransform = new TranslateTransform(0,direction*10000);
        Dispatcher.UIThread.Post(() =>
        {
            if (version != _switchVersion || token.IsCancellationRequested) return;
            UpdateLayout();
            if (Bounds.Height <= 0) { FinishTransition(); return; }
            _moving = true;
            var distance=direction*Bounds.Height;
            Move(_outgoing,1,1,0,-distance,MotionTokens.PageSwitch,MotionEasing.PageSlide);
            Move(_incoming,1,1,distance,0,MotionTokens.PageSwitch,MotionEasing.PageSlide);
            _ = CompleteAsync(version,token);
        }, DispatcherPriority.Loaded);
    }

    internal static void Move(Control control, double opacityFrom, double opacityTo, double yFrom, double yTo,
        TimeSpan duration, Avalonia.Animation.Easings.Easing easing)
    {
        control.Transitions = null;
        control.Opacity = opacityFrom;
        var translation = new TranslateTransform(0, yFrom);
        control.RenderTransform = translation;
        control.Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = duration, Easing = easing }];
        translation.Transitions = [new DoubleTransition { Property = TranslateTransform.YProperty, Duration = duration, Easing = easing }];
        control.Opacity = opacityTo;
        translation.Y = yTo;
    }

    private async Task CompleteAsync(int version, CancellationToken token)
    {
        try
        {
            await Task.Delay(MotionTokens.PageSwitch, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            // A canceled older switch must never clear the presenters owned by the latest navigation.
            if (version == _switchVersion) FinishTransition();
        }
    }
}
