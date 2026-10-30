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
        CategoryTabs.LayoutUpdated+=(_,_)=>CategoryHighlight.Follow(CategoryTabs);
        DataContextChanged+=(_,_)=>BindCategorySelection();
        AttachedToVisualTree+=(_,_)=>BindCategorySelection();
        DetachedFromVisualTree+=(_,_)=>{if(_categoryModel is not null)_categoryModel.PropertyChanged-=CategoryChanged;_categoryModel=null;};
        ResourceResultsScroll.ScrollChanged+=(_,e)=> {if(e.OffsetDelta.Y>0) TryLoadMore();};
        ResourceResultsScroll.AddHandler(PointerWheelChangedEvent,(_,e)=>
        {if(e.Delta.Y<0) Dispatcher.UIThread.Post(TryLoadMore);},RoutingStrategies.Bubble,true);
    }
    private DownloadViewModel? _categoryModel;
    private void BindCategorySelection()
    {
        if(_categoryModel is not null)_categoryModel.PropertyChanged-=CategoryChanged;
        _categoryModel=(DataContext as MainViewModel)?.Download;
        if(_categoryModel is not null)_categoryModel.PropertyChanged+=CategoryChanged;
    }
    private void CategoryChanged(object? sender,System.ComponentModel.PropertyChangedEventArgs e)
    {if(e.PropertyName==nameof(DownloadViewModel.SelectedCategory))Dispatcher.UIThread.Post(()=>CategoryHighlight.Follow(CategoryTabs),DispatcherPriority.Loaded);}
    private void TryLoadMore()
    {
        if(DataContext is not MainViewModel { Download:var vm }||!ResourceResultsScroll.IsEffectivelyVisible||!vm.CanAutoLoadMore) return;
        var remaining=ResourceResultsScroll.Extent.Height-ResourceResultsScroll.Viewport.Height-ResourceResultsScroll.Offset.Y;
        if(remaining<180) vm.LoadMoreResourcesCommand.Execute(null);
    }
}
