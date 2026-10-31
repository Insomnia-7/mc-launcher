using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;

namespace Launcher.Desktop.Controls;

/// <summary>Measures its child at full height and reveals a clipped fraction without scaling text.</summary>
public sealed class RevealPanel : Decorator
{
    public static readonly StyledProperty<bool> IsOpenProperty = AvaloniaProperty.Register<RevealPanel,bool>(nameof(IsOpen));
    public static readonly StyledProperty<double> ProgressProperty = AvaloniaProperty.Register<RevealPanel,double>(nameof(Progress));
    public bool IsOpen { get=>GetValue(IsOpenProperty);set=>SetValue(IsOpenProperty,value); }
    public double Progress { get=>GetValue(ProgressProperty);set=>SetValue(ProgressProperty,value); }
    private bool _attached;
    static RevealPanel() => AffectsMeasure<RevealPanel>(ProgressProperty);
    public RevealPanel() => ClipToBounds=true;
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if(change.Property==IsOpenProperty) UpdateReveal();
        if(change.Property==ProgressProperty && Child is {} child) child.IsVisible=IsOpen||Progress>0;
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);MotionPreferences.Changed+=PreferenceChanged;
        UpdateReveal();_attached=true;
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    { _attached=false;MotionPreferences.Changed-=PreferenceChanged;Transitions=null;base.OnDetachedFromVisualTree(e); }
    private void PreferenceChanged() { if(!MotionPreferences.Enabled) UpdateReveal(); }
    private void UpdateReveal()
    {
        if(Child is {} child && IsOpen)child.IsVisible=true;
        IsHitTestVisible=IsOpen;
        Transitions=_attached&&MotionPreferences.Enabled ? new Transitions {new DoubleTransition{Property=ProgressProperty,Duration=MotionTiming.Expand,Easing=new CubicEaseOut()}}:null;
        Progress=IsOpen?1:0;
        InvalidateMeasure();
    }
    protected override Size MeasureOverride(Size availableSize)
    {
        if(Child is not {} child)return default;
        if(!IsOpen&&Progress<=0){child.IsVisible=false;return default;}
        child.IsVisible=true;child.Measure(new Size(availableSize.Width,double.PositiveInfinity));
        return new Size(child.DesiredSize.Width,child.DesiredSize.Height*Math.Clamp(Progress,0,1));
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(0,0,finalSize.Width,Child.DesiredSize.Height));return finalSize;
    }
}
