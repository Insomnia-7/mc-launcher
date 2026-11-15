using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;

namespace Launcher.Desktop.Controls;

public sealed class FeedbackIcon : Control
{
    public static readonly StyledProperty<FeedbackState> StateProperty=AvaloniaProperty.Register<FeedbackIcon,FeedbackState>(nameof(State));
    public static readonly StyledProperty<IBrush?> ForegroundProperty=TemplatedControl.ForegroundProperty.AddOwner<FeedbackIcon>();
    public FeedbackState State {get=>GetValue(StateProperty);set=>SetValue(StateProperty,value);}
    public IBrush? Foreground {get=>GetValue(ForegroundProperty);set=>SetValue(ForegroundProperty,value);}
    private static readonly Geometry Spinner=Geometry.Parse("M12 4 A8 8 0 1 1 4 12");
    private static readonly Geometry Check=Geometry.Parse("M5 12 L10 17 L19 7");
    private static readonly Geometry Error=Geometry.Parse("M12 4 L21 20 L3 20 Z M12 9 L12 14 M12 17 L12 17.2");
    private readonly Stopwatch _clock=new();
    private readonly DispatcherTimer _timer=new(){Interval=TimeSpan.FromMilliseconds(16)};
    private bool _attached;
    public bool SpinnerRunning=>_timer.IsEnabled;
    public double RotationAngle=>_clock.Elapsed.TotalMilliseconds%900/900*360;
    public FeedbackIcon(){IsHitTestVisible=false;_timer.Tick+=(_,_)=>InvalidateVisual();}
    static FeedbackIcon()=>AffectsRender<FeedbackIcon>(StateProperty,ForegroundProperty);
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e){base.OnAttachedToVisualTree(e);_attached=true;MotionPreferences.Changed+=UpdateSpinner;UpdateSpinner();}
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e){_attached=false;MotionPreferences.Changed-=UpdateSpinner;_timer.Stop();_clock.Reset();base.OnDetachedFromVisualTree(e);}
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e){base.OnPropertyChanged(e);if(e.Property==StateProperty)UpdateSpinner();}
    private void UpdateSpinner()
    {
        if(_attached && State==FeedbackState.Loading && MotionPreferences.Enabled){_clock.Start();_timer.Start();}
        else{_timer.Stop();_clock.Reset();InvalidateVisual();}
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context);if(State==FeedbackState.None || Foreground is null)return;
        var scale=Math.Min(Bounds.Width,Bounds.Height)/24;
        if(scale<=0)return;
        using var fit=context.PushTransform(Matrix.CreateScale(scale,scale)*Matrix.CreateTranslation((Bounds.Width-24*scale)/2,(Bounds.Height-24*scale)/2));
        using var rotation=context.PushTransform(State==FeedbackState.Loading?Matrix.CreateTranslation(-12,-12)*Matrix.CreateRotation(RotationAngle*Math.PI/180)*Matrix.CreateTranslation(12,12):Matrix.Identity);
        context.DrawGeometry(null,new Pen(Foreground,2,lineCap:PenLineCap.Round,lineJoin:PenLineJoin.Round),State==FeedbackState.Loading?Spinner:State==FeedbackState.Success?Check:Error);
    }
}
