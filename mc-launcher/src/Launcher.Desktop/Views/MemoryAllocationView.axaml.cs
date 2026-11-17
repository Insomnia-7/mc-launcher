using Avalonia.Controls;
using System.ComponentModel;
using Launcher.App.ViewModels;
namespace Launcher.Desktop.Views;
public sealed partial class MemoryAllocationView:UserControl
{
    private MainViewModel? _model;
    public MemoryAllocationView()
    {
        InitializeComponent();
        DataContextChanged+=(_,_)=>Connect(DataContext as MainViewModel);
        AttachedToVisualTree+=(_,_)=>Connect(DataContext as MainViewModel);
        DetachedFromVisualTree+=(_,_)=>Connect(null);
    }
    private void Connect(MainViewModel? model)
    {
        if(_model is not null)_model.PropertyChanged-=Changed;
        _model=model;
        if(_model is not null){_model.PropertyChanged+=Changed;MemoryBar.ColumnDefinitions=_model.MemorySegments;}
    }
    private void Changed(object? sender,PropertyChangedEventArgs e)
    {if(e.PropertyName==nameof(MainViewModel.MemorySegments)&&_model is not null)MemoryBar.ColumnDefinitions=_model.MemorySegments;}
}
