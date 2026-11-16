using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

namespace Launcher.Desktop;

public sealed partial class MainWindow
{
    private sealed record StartupPart(Control Control,int Delay,double X=0,double Y=0,bool Move=true)
    {public bool HitTest {get;}=Control.IsHitTestVisible;}
    private readonly List<StartupPart> _startupParts=[];
    private CancellationTokenSource? _startupCts;
    private TaskCompletionSource? _startupCompletion;
    private bool _startupStarted;
    internal bool IsStartupEntranceActive=>_startupParts.Count>0;
    internal Task StartupEntranceCompletion=>_startupCompletion?.Task??Task.CompletedTask;

    internal void PrepareStartupEntrance()
    {
        if(!MotionPreferences.Enabled||_model?.ShellPage!="home")return;
        var step=MotionTokens.StartupStaggerMs;
        _startupParts.AddRange([new(WindowHeader,0,0,-12),new(SidebarBrandHost,step,-72),new(SidebarBottomText,step,-72),new(StatusBar,0,0,8),new(NavSelection,2*step,Move:false),new(NavIndicator,2*step,Move:false)]);
        var names=new[]{"HomeNav","LibraryNav","MultiplayerNav","NetworkNav","DiscoverNav","TaskCenterEntry","SettingsEntry"};
        for(var i=0;i<names.Length;i++)_startupParts.Add(new(this.FindControl<Button>(names[i])!,2*step+i*step,-72));
        var cards=new[]{"HomeHeroBanner","HomeQuickLaunchCard","HomeNetworkStatusCard","HomeInstancesCard","HomeShortcutsCard"};
        for(var i=0;i<cards.Length;i++)_startupParts.Add(new(_home.FindControl<Border>(cards[i])!,2*step+i*step,90));
        foreach(var part in _startupParts)
        {
            var c=part.Control;c.Transitions=null;c.Opacity=0;c.IsHitTestVisible=false;
            if(part.Move)c.RenderTransform=TransformOperations.Parse(FormattableString.Invariant($"translate({part.X}px,{part.Y}px)"));
        }
        _startupCompletion=new(TaskCreationOptions.RunContinuationsAsynchronously);_startupCts=new();
        Opened+=StartStartupEntrance;
    }
    private void StartStartupEntrance(object? sender,EventArgs e)
    {
        Opened-=StartStartupEntrance;
        Dispatcher.UIThread.Post(async ()=>
        {
            if(_startupCts is not {} cts)return;
            _startupStarted=true;
            try
            {
                await Task.WhenAll(_startupParts.Select(async part=>
                {
                    await Task.Delay(part.Delay,cts.Token);
                    var c=part.Control;
                    c.Transitions=[new DoubleTransition{Property=OpacityProperty,Duration=MotionTokens.StartupPart,Easing=new CubicEaseOut()}];
                    if(part.Move)c.Transitions.Add(new TransformOperationsTransition{Property=RenderTransformProperty,Duration=MotionTokens.StartupPart,Easing=new CubicEaseOut()});
                    c.Opacity=1;if(part.Move)c.RenderTransform=TransformOperations.Identity;c.IsHitTestVisible=part.HitTest;
                    await Task.Delay(MotionTokens.StartupPart,cts.Token);
                }));
            }
            catch(OperationCanceledException)when(cts.IsCancellationRequested){}
            finally{FinishStartupEntrance();cts.Dispose();}
        },DispatcherPriority.Loaded);
    }
    internal void FinishStartupEntrance()
    {
        Opened-=StartStartupEntrance;
        var cts=_startupCts;_startupCts=null;cts?.Cancel();if(!_startupStarted)cts?.Dispose();
        foreach(var part in _startupParts)
        {
            var c=part.Control;c.Transitions=null;c.ClearValue(OpacityProperty);
            if(part.Move)c.ClearValue(RenderTransformProperty);
            c.IsHitTestVisible=part.HitTest;c.ClearValue(TransitionsProperty);
        }
        _startupParts.Clear();_startupStarted=false;_startupCompletion?.TrySetResult();
    }
}
