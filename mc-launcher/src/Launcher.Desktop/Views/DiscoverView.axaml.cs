using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Launcher.App.ViewModels;
namespace Launcher.Desktop.Views;
public sealed partial class DiscoverView : UserControl
{
    public DiscoverView()
    {
        InitializeComponent();
        ResourceResultsScroll.ScrollChanged+=(_,e)=> {if(e.OffsetDelta.Y>0) TryLoadMore();};
        ResourceResultsScroll.AddHandler(PointerWheelChangedEvent,(_,e)=>
        {if(e.Delta.Y<0) Dispatcher.UIThread.Post(TryLoadMore);},RoutingStrategies.Bubble,true);
    }
    private void TryLoadMore()
    {
        if(DataContext is not MainViewModel { Download:var vm }||!ResourceResultsScroll.IsEffectivelyVisible||!vm.CanAutoLoadMore) return;
        var remaining=ResourceResultsScroll.Extent.Height-ResourceResultsScroll.Viewport.Height-ResourceResultsScroll.Offset.Y;
        if(remaining<180) vm.LoadMoreResourcesCommand.Execute(null);
    }
}
