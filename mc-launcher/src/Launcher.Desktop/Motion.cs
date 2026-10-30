using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Styling;

namespace Launcher.Desktop;

public static class MotionPreferences
{
    private static string _mode = "smooth";
    public static event Action? Changed;
    public static string Mode { get => _mode; set { _mode = value; Changed?.Invoke(); } }
    public static bool Enabled => Mode == "smooth" || (Mode == "system" && SystemAnimationsEnabled());
    private static bool SystemAnimationsEnabled() => !OperatingSystem.IsWindows() || !SystemParametersInfo(0x1042,0,out var enabled,0) || enabled;
    [DllImport("user32.dll", SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action,uint parameter,[MarshalAs(UnmanagedType.Bool)] out bool value,uint flags);
}

public sealed class SoftSpringEasing : Easing
{
    public override double Ease(double p) => p <= 0 ? 0 : p >= 1 ? 1 : 1 - Math.Exp(-8 * p) * Math.Cos(10 * p);
}

public sealed class WorldPageTransition : IPageTransition
{
    public async Task Start(Visual? from, Visual? to, bool forward, CancellationToken cancellationToken)
    {
        if (from == to) return;
        if (!MotionPreferences.Enabled)
        {
            if (from is not null) from.IsVisible = false;
            if (to is not null) { to.IsVisible = true; to.Opacity = 1; }
            return;
        }
        if (to is not null) to.IsVisible = true;
        try
        {
            var tasks = new List<Task>();
            if (from is Control old) tasks.Add(Motion.Animate(old, 1, 0, 0, forward ? -8 : 8, 150, cancellationToken));
            if (to is Control next) tasks.Add(Motion.Animate(next, 0, 1, forward ? 12 : -12, 0, 200, cancellationToken));
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            // A canceled transition must not hide a page reused by a newer navigation.
            if (!cancellationToken.IsCancellationRequested)
            {
                if (from is not null) { from.IsVisible = false; from.Opacity = 1; from.RenderTransform = TransformOperations.Identity; }
                if (to is not null) { to.Opacity = 1; to.RenderTransform = TransformOperations.Identity; }
            }
        }
    }
}

public sealed class Motion : AvaloniaObject
{
    private sealed class State { public CancellationTokenSource? Cancellation; }
    private static readonly ConditionalWeakTable<Control, State> States = new();
    public static readonly AttachedProperty<bool> IsOpenProperty = AvaloniaProperty.RegisterAttached<Motion, Control, bool>("IsOpen");
    static Motion() => IsOpenProperty.Changed.AddClassHandler<Control>((control, e) => SetOpen(control, e.GetNewValue<bool>()));
    public static bool GetIsOpen(Control control) => control.GetValue(IsOpenProperty);
    public static void SetIsOpen(Control control, bool value) => control.SetValue(IsOpenProperty, value);

    private static async void SetOpen(Control control, bool open)
    {
        var state = States.GetOrCreateValue(control);
        state.Cancellation?.Cancel(); state.Cancellation?.Dispose();
        var cancellation = state.Cancellation = new CancellationTokenSource();
        var surface = control is Panel panel ? panel.Children.OfType<Border>().FirstOrDefault() : null;
        control.IsHitTestVisible = open;
        if (!MotionPreferences.Enabled) { control.IsVisible = open; control.Opacity = 1; if(surface is not null) surface.RenderTransform=TransformOperations.Identity; return; }
        if (open) control.IsVisible = true;
        try
        {
            var fade=Animate(control, open ? 0 : control.Opacity, open ? 1 : 0, 0, 0, open ? 180 : 120, cancellation.Token);
            if(surface is not null) await Task.WhenAll(fade,Animate(surface,1,1,open ? 14 : 0,open ? 0 : 8,open ? 200 : 120,cancellation.Token));
            else await fade;
            if (state.Cancellation == cancellation) { control.IsVisible = open; control.Opacity = 1; if(surface is not null) surface.RenderTransform=TransformOperations.Identity; }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    internal static Task Animate(Control target, double opacityFrom, double opacityTo, double yFrom, double yTo, int milliseconds, CancellationToken token)
    {
        var animation = new Animation { Duration = TimeSpan.FromMilliseconds(milliseconds), Easing = new CubicEaseOut(), FillMode = FillMode.Forward };
        animation.Children.Add(new KeyFrame { Cue = new Cue(0), Setters = { new Setter(Visual.OpacityProperty, opacityFrom) } });
        animation.Children.Add(new KeyFrame { Cue = new Cue(1), Setters = { new Setter(Visual.OpacityProperty, opacityTo) } });
        if (yFrom == yTo) return animation.RunAsync(target, token);
        var transform = new TranslateTransform(0, yTo);
        target.RenderTransform = transform;
        var slide = new Animation { Duration = animation.Duration, Easing = new CubicEaseOut(), FillMode = FillMode.Forward };
        slide.Children.Add(new KeyFrame { Cue = new Cue(0), Setters = { new Setter(TranslateTransform.YProperty, yFrom) } });
        slide.Children.Add(new KeyFrame { Cue = new Cue(1), Setters = { new Setter(TranslateTransform.YProperty, yTo) } });
        // Avalonia's TransformAnimator resolves the transform on a Visual; its target is the control.
        return Task.WhenAll(animation.RunAsync(target, token), slide.RunAsync(target, token));
    }
}
