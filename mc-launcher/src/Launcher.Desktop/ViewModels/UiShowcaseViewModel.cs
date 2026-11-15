using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.Application.Settings;

namespace Launcher.App.ViewModels;

/// <summary>Preview-only state; no game, account, download or room service is invoked.</summary>
public sealed partial class UiShowcaseViewModel(MainViewModel host) : ObservableObject, IDisposable
{
    public MainViewModel Host {get;}=host;
    public ToastManager Toasts {get;}=new();
    private bool _active,_disposed;
    private CancellationTokenSource? _demo;
    [ObservableProperty] private bool _isDialogOpen;
    [ObservableProperty] private bool _simulateFailure;
    [ObservableProperty] private bool _demoSucceeded;
    [ObservableProperty] private bool _demoFailed;
    [ObservableProperty] private string _demoStatus="等待运行预览操作。";
    [ObservableProperty] private string _dialogText="这是一条预览内容";
    public bool CanPreview=>Host.IsDevShowcaseEnabled && _active && !_disposed;
    public void Activate(){if(_disposed)return;_active=true;NotifyCommands();}
    public void Deactivate()
    {
        _active=false;_demo?.Cancel();IsDialogOpen=false;Toasts.Clear();
        DemoSucceeded=false;DemoFailed=false;DemoStatus="等待运行预览操作。";NotifyCommands();
    }
    private void NotifyCommands()
    {
        RunDemoCommand.NotifyCanExecuteChanged();ShowToastCommand.NotifyCanExecuteChanged();
        ShowAllToastsCommand.NotifyCanExecuteChanged();OpenDialogCommand.NotifyCanExecuteChanged();
        CloseDialogCommand.NotifyCanExecuteChanged();ConfirmDialogCommand.NotifyCanExecuteChanged();
        SetThemeCommand.NotifyCanExecuteChanged();ToggleSidebarCommand.NotifyCanExecuteChanged();
    }
    [RelayCommand(CanExecute=nameof(CanPreview))] private void SetTheme(string? mode)
    {if(CanPreview)Host.SetThemeMode(mode switch{"light"=>ThemeMode.Light,"system"=>ThemeMode.System,_=>ThemeMode.Dark});}
    [RelayCommand(CanExecute=nameof(CanPreview))] private void ToggleSidebar(){if(CanPreview)Host.IsSidebarCollapsed=!Host.IsSidebarCollapsed;}
    [RelayCommand(CanExecute=nameof(CanPreview))] private void ShowToast(string? level)
    {
        if(!CanPreview)return;
        var kind=level switch{"success"=>ToastLevel.Success,"warning"=>ToastLevel.Warning,"error"=>ToastLevel.Error,_=>ToastLevel.Info};
        Toasts.Show(kind switch{ToastLevel.Success=>"预览：操作已完成。",ToastLevel.Warning=>"预览：请确认后继续。",ToastLevel.Error=>"预览：操作未完成，可重试。",_=>"预览：这是一条普通提示。"},kind,
            ()=>{if(CanPreview)DemoStatus="已查看预览通知。";},"查看预览");
    }
    [RelayCommand(CanExecute=nameof(CanPreview))] private void ShowAllToasts()
    {if(CanPreview)foreach(var level in new[]{"info","success","warning","error"})ShowToast(level);}
    [RelayCommand(CanExecute=nameof(CanPreview))] private void OpenDialog(){if(CanPreview)IsDialogOpen=true;}
    [RelayCommand(CanExecute=nameof(CanPreview))] private void CloseDialog()=>IsDialogOpen=false;
    [RelayCommand(CanExecute=nameof(CanPreview))] private void ConfirmDialog(){if(!CanPreview)return;IsDialogOpen=false;ShowToast("success");}
    [RelayCommand(CanExecute=nameof(CanPreview))] private async Task RunDemoAsync()
    {
        if(!CanPreview || _demo is not null)return;
        using var operation=new CancellationTokenSource();_demo=operation;
        DemoSucceeded=false;DemoFailed=false;DemoStatus="正在模拟操作…";var fail=SimulateFailure;
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(900),operation.Token);
            if(!CanPreview || operation.IsCancellationRequested)return;
            DemoSucceeded=!fail;DemoFailed=fail;DemoStatus=fail?"预览操作失败：可再次运行。":"预览操作成功。";
        }
        catch(OperationCanceledException) when(operation.IsCancellationRequested){}
        finally{if(ReferenceEquals(_demo,operation))_demo=null;}
    }
    public void Dispose(){if(_disposed)return;Deactivate();_disposed=true;Toasts.Dispose();}
}
