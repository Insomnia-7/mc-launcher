using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Launcher.Desktop.Controls;

/// <summary>Cards fade out before the next page fades in; old and new text never overlap.</summary>
public sealed class AnimatedContentHost : Grid
{
    public static readonly StyledProperty<Control?> ContentProperty = AvaloniaProperty.Register<AnimatedContentHost,Control?>(nameof(Content));
    private readonly ContentPresenter _outgoing=new(){IsVisible=false,IsHitTestVisible=false,ClipToBounds=true};
    private readonly ContentPresenter _incoming=new(){ClipToBounds=true};
    private CancellationTokenSource? _switchCts;
    private int _switchVersion;
    private bool _attached;
    public bool IsTransitioning {get;private set;}
    public Control? Content {get=>GetValue(ContentProperty);set=>SetValue(ContentProperty,value);}
    public AnimatedContentHost(){ClipToBounds=true;Children.Add(_outgoing);Children.Add(_incoming);}
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if(change.Property==ContentProperty)SwitchPage(change.GetOldValue<Control?>(),change.GetNewValue<Control?>());
        else if(change.Property==BoundsProperty&&IsTransitioning)FinishTransition();
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e){base.OnAttachedToVisualTree(e);_attached=true;MotionPreferences.Changed+=MotionChanged;}
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e){_attached=false;MotionPreferences.Changed-=MotionChanged;FinishTransition();base.OnDetachedFromVisualTree(e);}
    private void MotionChanged(){if(!MotionPreferences.Enabled)FinishTransition();}
    public void FinishTransition()
    {
        ++_switchVersion;IsTransitioning=false;_switchCts?.Cancel();_switchCts?.Dispose();_switchCts=null;
        Clear(_outgoing);Normalize(_incoming);_incoming.IsVisible=_incoming.Content is not null;_incoming.IsHitTestVisible=true;
    }
    private static void Normalize(Control presenter){presenter.Transitions=null;presenter.Opacity=1;presenter.RenderTransform=new TranslateTransform();}
    private static void Clear(ContentPresenter presenter){Normalize(presenter);presenter.Content=null;presenter.UpdateChild();presenter.IsVisible=false;}
    private void SwitchPage(Control? previous,Control? next)
    {
        FinishTransition();Clear(_incoming);
        _incoming.Background=_outgoing.Background=Background;
        var animate=_attached&&MotionPreferences.Enabled&&previous is not null&&next is not null;
        if(animate){_outgoing.Content=previous;_outgoing.IsVisible=true;_outgoing.UpdateChild();}
        _incoming.Content=next;_incoming.IsVisible=next is not null;_incoming.UpdateChild();
        if(!animate)return;
        IsTransitioning=true;_incoming.Opacity=0;_incoming.IsHitTestVisible=false;
        _switchCts=new CancellationTokenSource();var token=_switchCts.Token;var version=_switchVersion;
        Dispatcher.UIThread.Post(()=>{if(version==_switchVersion&&!token.IsCancellationRequested)_=FadePagesAsync(previous!,next!,version,token);},DispatcherPriority.Loaded);
    }
    private static bool IsCard(Control control)=>control.Classes.Contains("card")||control.Classes.Contains("tile")||control is ListBoxItem||control.Name=="HomeHeroBanner";
    internal static Control[] FadeTargets(Control page)
    {
        var cards=page.GetVisualDescendants().OfType<Control>().Where(c=>c.IsEffectivelyVisible&&IsCard(c))
            .Where(c=>!c.GetVisualAncestors().OfType<Control>().TakeWhile(p=>p!=page).Any(IsCard)).ToArray();
        return cards.Length>0?cards:[page];
    }
    private static Task FadeCardsAsync(Control page,bool entering,CancellationToken token)
    {
        return Task.WhenAll(FadeTargets(page).Select((card,index)=>
        {
            var delay=entering?Math.Min(index*10,60):0;
            var animation=new Animation{Duration=MotionTokens.PageSwitch-TimeSpan.FromMilliseconds(delay),Delay=TimeSpan.FromMilliseconds(delay),Easing=new SineEaseInOut(),FillMode=FillMode.Backward};
            animation.Children.Add(new KeyFrame{Cue=new Cue(0),Setters={new Setter(OpacityProperty,entering?0:card.Opacity)}});
            animation.Children.Add(new KeyFrame{Cue=new Cue(1),Setters={new Setter(OpacityProperty,entering?card.Opacity:0)}});
            // Animation priority leaves bindings, disabled opacity and hover styles intact on completion/cancel.
            return animation.RunAsync(card,token);
        }));
    }
    private async Task FadePagesAsync(Control previous,Control next,int version,CancellationToken token)
    {
        try
        {
            await FadeCardsAsync(previous,false,token);
            if(version!=_switchVersion||token.IsCancellationRequested)return;
            Clear(_outgoing);_incoming.Opacity=1;_incoming.IsHitTestVisible=true;
            await FadeCardsAsync(next,true,token);
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
