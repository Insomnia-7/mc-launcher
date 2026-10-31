using System.ComponentModel;
using Avalonia.Controls;
using Launcher.App.ViewModels;
namespace Launcher.Desktop.Views;
public sealed partial class GameDetailView:UserControl
{
    private MainViewModel? _model;
    private readonly Dictionary<int,Control> _pages=[];
    public GameDetailView()
    {
        InitializeComponent();
        DataContextChanged+=(_,_)=>
        {
            if(_model is not null)_model.PropertyChanged-=Changed;
            _model=DataContext as MainViewModel;
            if(_model is not null)_model.PropertyChanged+=Changed;
            UpdateTab();
        };
    }
    private void Changed(object? sender,PropertyChangedEventArgs e){if(e.PropertyName==nameof(MainViewModel.DetailTab))UpdateTab();}
    private void UpdateTab()
    {
        if(_model is null||_model.DetailTab==0){DetailTabHost.Content=null;return;}
        if(!_pages.TryGetValue(_model.DetailTab,out var page))
        {
            page=_model.DetailTab switch {1=>new InstanceModsView(),2=>new InstanceHistoryView(),3=>new InstanceSettingsView(),5=>new InstanceModpackView(),_=>new InstanceDiagnosticsView()};
            _pages[_model.DetailTab]=page;
        }
        DetailTabHost.Content=page;
    }
}
