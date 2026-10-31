using Avalonia.Controls;
using System.ComponentModel;
using Launcher.App.ViewModels;
namespace Launcher.Desktop.Views;
public sealed partial class PreferencesView:UserControl
{
    private MainViewModel? _model;
    public PreferencesView()
    {
        InitializeComponent();
        DataContextChanged+=(_,_)=>
        {
            if(_model is not null)_model.PropertyChanged-=Changed;
            _model=DataContext as MainViewModel;
            if(_model is not null)_model.PropertyChanged+=Changed;
            LoadSyncPage();
        };
    }
    private void Changed(object? sender,PropertyChangedEventArgs e){if(e.PropertyName==nameof(MainViewModel.ShowsSettingsSync))LoadSyncPage();}
    private void LoadSyncPage(){if(_model?.ShowsSettingsSync==true)SettingsSyncHost.Content??=new GameSettingsSyncView();}
}
