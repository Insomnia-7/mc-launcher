using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Launcher.Desktop.Controls;

/// <summary>Keeps the scrim mounted until exit completes; reversals belong to the latest revision.</summary>
public sealed class AnimatedDialog : Grid
{
    public static readonly StyledProperty<bool> IsOpenProperty=AvaloniaProperty.Register<AnimatedDialog,bool>(nameof(IsOpen));
    public static readonly StyledProperty<IBrush?> ScrimBrushProperty=AvaloniaProperty.Register<AnimatedDialog,IBrush?>(nameof(ScrimBrush));
    private readonly Border _scrim=new();
    private CancellationTokenSource? _transition;
    private bool _attached;
    private int _version;
    private Control? _previousFocus;
    public bool IsOpen {get=>GetValue(IsOpenProperty);set=>SetValue(IsOpenProperty,value);}
    public IBrush? ScrimBrush {get=>GetValue(ScrimBrushProperty);set=>SetValue(ScrimBrushProperty,value);}
    public Border? Surface=>Children.OfType<Border>().LastOrDefault(b=>!ReferenceEquals(b,_scrim));
    public double ScrimOpacity=>_scrim.Opacity;
    public AnimatedDialog(){IsVisible=false;KeyboardNavigation.SetTabNavigation(this,KeyboardNavigationMode.Cycle);Children.Add(_scrim);}
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if(change.Property==ScrimBrushProperty)_scrim.Background=ScrimBrush;
        if(change.Property==IsOpenProperty)UpdateDialog();
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {base.OnAttachedToVisualTree(e);_attached=true;MotionPreferences.Changed+=PreferenceChanged;if(IsOpen)IsVisible=false;UpdateDialog();}
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {_attached=false;MotionPreferences.Changed-=PreferenceChanged;CancelTransition();Settle();base.OnDetachedFromVisualTree(e);}
    private void CancelTransition(){++_version;_transition?.Cancel();_transition?.Dispose();_transition=null;}
    private void PreferenceChanged(){if(!MotionPreferences.Enabled){CancelTransition();Settle();}}
    private void Settle()
    {
        _scrim.Transitions=null;_scrim.Opacity=1;
        if(Surface is {} surface){surface.Transitions=null;surface.Opacity=1;surface.RenderTransform=TransformOperations.Identity;surface.IsHitTestVisible=IsOpen;}
        IsVisible=IsOpen;
        if(!IsOpen)RestoreFocus();
    }
    private void RestoreFocus()
    {
        if(_previousFocus is {IsEffectivelyVisible:true} focus && TopLevel.GetTopLevel(this) is {} top && (!top.GetVisualDescendants().OfType<AnimatedDialog>().Any(d=>d.IsOpen) || focus.GetVisualAncestors().OfType<AnimatedDialog>().Any(d=>d.IsOpen)))focus.Focus();
        _previousFocus=null;
    }
    private void UpdateDialog()
    {
        CancelTransition();
        if(Surface is not {} surface || !_attached){Settle();return;}
        var entering=IsOpen&&!IsVisible;
        if(IsOpen)
        {
            if(entering)
            {
                _previousFocus=TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
                surface.Transitions=null;surface.Opacity=0;surface.RenderTransform=TransformOperations.Parse("scale(0.97) translate(0px,6px)");
                _scrim.Transitions=null;_scrim.Opacity=0;IsVisible=true;
            }
            var focusVersion=_version;
            Dispatcher.UIThread.Post(()=>
            {
                if(IsOpen && focusVersion==_version)surface.GetVisualDescendants().OfType<Control>().FirstOrDefault(c=>c.Focusable&&c.IsEffectivelyVisible&&c.IsEffectivelyEnabled)?.Focus(NavigationMethod.Tab);
            },DispatcherPriority.Loaded);
        }
        surface.IsHitTestVisible=IsOpen;
        if(!MotionPreferences.Enabled){Settle();return;}
        if(entering)
        {
            var version=_version;
            // First materialization can include expensive layout and font initialization.
            // Start the clock after it, keeping the initial transparent frame until Loaded.
            Dispatcher.UIThread.Post(()=>{if(version==_version)AnimateSurface(surface);},DispatcherPriority.Loaded);
        }
        else AnimateSurface(surface);
    }
    private void AnimateSurface(Border surface)
    {
        if(!MotionPreferences.Enabled){Settle();return;}
        var duration=IsOpen?MotionTokens.Dialog:MotionTokens.DialogExit;
        if(surface.Transitions is not {Count:2})surface.Transitions=[new DoubleTransition{Property=OpacityProperty},new TransformOperationsTransition{Property=RenderTransformProperty}];
        foreach(var transition in surface.Transitions.OfType<TransitionBase>()){transition.Duration=duration;transition.Easing=MotionEasing.For(IsOpen);}
        if(_scrim.Transitions is not {Count:1})_scrim.Transitions=[new DoubleTransition{Property=OpacityProperty}];
        ((DoubleTransition)_scrim.Transitions[0]).Duration=IsOpen?MotionTokens.DialogScrim:MotionTokens.DialogExit;((DoubleTransition)_scrim.Transitions[0]).Easing=MotionEasing.For(IsOpen);
        _scrim.Opacity=IsOpen?1:0;surface.Opacity=IsOpen?1:0;
        surface.RenderTransform=IsOpen?TransformOperations.Identity:TransformOperations.Parse("scale(0.97) translate(0px,6px)");
        _transition=new CancellationTokenSource();_ = CompleteAsync(_version,duration,_transition.Token);
    }
    private async Task CompleteAsync(int version,TimeSpan duration,CancellationToken token)
    {
        try{await Task.Delay(duration,token);}
        catch(OperationCanceledException) when(token.IsCancellationRequested){}
        finally
        {
            if(version==_version)
            {
                Settle();
                _transition?.Dispose();_transition=null;
            }
        }
    }
}
