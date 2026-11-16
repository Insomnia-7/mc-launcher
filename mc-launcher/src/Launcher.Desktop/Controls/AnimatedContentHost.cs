using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Launcher.Desktop.Controls;

/// <summary>Fixed page layouts fade/scale separately; the old page never reflows into the new margins.</summary>
public sealed class AnimatedContentHost : Grid
{
    public static readonly StyledProperty<Control?> ContentProperty = AvaloniaProperty.Register<AnimatedContentHost,Control?>(nameof(Content));
    private readonly ContentPresenter _outgoing=new(){IsVisible=false,IsHitTestVisible=false,ClipToBounds=true};
    private readonly ContentPresenter _incoming=new(){ClipToBounds=true};
    private CancellationTokenSource? _switchCts;
    private Thickness _contentMargin;
    private int _switchVersion;
    private bool _attached;
    public bool IsTransitioning {get;private set;}
    public Control? Content {get=>GetValue(ContentProperty);set=>SetValue(ContentProperty,value);}
    public AnimatedContentHost(){ClipToBounds=true;Children.Add(_outgoing);Children.Add(_incoming);}
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if(change.Property==ContentProperty)SwitchPage(change.GetNewValue<Control?>());
    }
    public void SetPage(Control? page,Thickness margin)
    {
        _contentMargin=margin;
        if(ReferenceEquals(Content,page))
        {
            if(_incoming.Margin!=margin){FinishTransition();_incoming.Margin=margin;}
        }
        else Content=page;
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e){base.OnAttachedToVisualTree(e);_attached=true;MotionPreferences.Changed+=MotionChanged;}
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e){_attached=false;MotionPreferences.Changed-=MotionChanged;FinishTransition();base.OnDetachedFromVisualTree(e);}
    private void MotionChanged(){if(!MotionPreferences.Enabled)FinishTransition();}
    public void FinishTransition()
    {
        ++_switchVersion;IsTransitioning=false;Clear(_outgoing);
        _switchCts?.Cancel();_switchCts?.Dispose();_switchCts=null;
        Normalize(_incoming);_incoming.IsVisible=_incoming.Content is not null;_incoming.IsHitTestVisible=true;
    }
    private static void Normalize(Control presenter){presenter.Transitions=null;presenter.Opacity=1;presenter.RenderTransform=new TranslateTransform();}
    private static void Clear(ContentPresenter presenter){presenter.IsVisible=false;presenter.Content=null;presenter.UpdateChild();Normalize(presenter);presenter.Width=double.NaN;presenter.Height=double.NaN;presenter.Margin=default;}
    private void SwitchPage(Control? next)
    {
        var shown=_outgoing.Content is not null&&_outgoing.IsVisible?_outgoing:_incoming;
        var previous=shown.Content as Control;var bounds=shown.Bounds;var margin=shown.Margin;
        var opacity=shown.Opacity;var scale=shown.RenderTransform?.Value.M11??1;
        FinishTransition();Clear(_incoming);
        _incoming.Background=_outgoing.Background=Background;
        var animate=_attached&&MotionPreferences.Enabled&&previous is not null&&next is not null&&!ReferenceEquals(previous,next)&&bounds.Width>0&&bounds.Height>0;
        if(animate)
        {
            _outgoing.Margin=margin;_outgoing.Width=bounds.Width;_outgoing.Height=bounds.Height;
            _outgoing.HorizontalAlignment=Avalonia.Layout.HorizontalAlignment.Left;_outgoing.VerticalAlignment=Avalonia.Layout.VerticalAlignment.Top;
            _outgoing.Opacity=opacity;_outgoing.RenderTransformOrigin=RelativePoint.Center;_outgoing.RenderTransform=new ScaleTransform(scale,scale);
            _outgoing.Content=previous;_outgoing.IsVisible=true;_outgoing.UpdateChild();
        }
        _incoming.Margin=_contentMargin;
        _incoming.Content=next;_incoming.IsVisible=next is not null;_incoming.UpdateChild();
        if(!animate)return;
        IsTransitioning=true;_incoming.Opacity=0;_incoming.IsHitTestVisible=false;
        _switchCts=new CancellationTokenSource();var token=_switchCts.Token;var version=_switchVersion;
        Dispatcher.UIThread.Post(()=>{if(version==_switchVersion&&!token.IsCancellationRequested)_=FadePagesAsync(version,token);},DispatcherPriority.Loaded);
    }
    private static bool IsCard(Control control)=>control.Classes.Contains("card")||control.Classes.Contains("tile")||control is ListBoxItem||control.Name=="HomeHeroBanner";
    internal static Control[] FadeTargets(Control page)
    {
        var cards=page.GetVisualDescendants().OfType<Control>().Where(c=>c.IsEffectivelyVisible&&IsCard(c))
            .Where(c=>!c.GetVisualAncestors().OfType<Control>().TakeWhile(p=>p!=page).Any(IsCard)).ToArray();
        return cards.Length>0?cards:[page];
    }
    private static async Task FadePageAsync(ContentPresenter presenter,bool entering,CancellationToken token)
    {
        var from=entering?0:presenter.Opacity;var to=entering?1:0;
        var startScale=entering?0.985:presenter.RenderTransform!.Value.M11;var endScale=entering?1:0.985;
        presenter.Transitions=null;presenter.Opacity=from;presenter.RenderTransformOrigin=RelativePoint.Center;
        presenter.RenderTransform=TransformOperations.Parse(FormattableString.Invariant($"scale({startScale})"));
        presenter.Transitions=[new DoubleTransition{Property=OpacityProperty,Duration=MotionTokens.PageSwitch,Easing=new SineEaseInOut()},new TransformOperationsTransition{Property=RenderTransformProperty,Duration=MotionTokens.PageSwitch,Easing=new SineEaseInOut()}];
        // Private presenters own the endpoints, so removing a transition never restores a visible old page.
        presenter.Opacity=to;presenter.RenderTransform=TransformOperations.Parse(FormattableString.Invariant($"scale({endScale})"));
        await Task.Delay(MotionTokens.PageSwitch,token);
    }
    private async Task FadePagesAsync(int version,CancellationToken token)
    {
        try
        {
            await FadePageAsync(_outgoing,false,token);
            if(version!=_switchVersion||token.IsCancellationRequested)return;
            Clear(_outgoing);
            var entrance=FadePageAsync(_incoming,true,token);
            _incoming.IsHitTestVisible=true;
            await entrance;
        }
        catch(OperationCanceledException)when(token.IsCancellationRequested){}
        finally{if(version==_switchVersion)FinishTransition();}
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

}
