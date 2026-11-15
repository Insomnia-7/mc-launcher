using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.Desktop;

namespace Launcher.App.ViewModels;

public enum ToastLevel { Info, Success, Warning, Error }

public sealed partial class ToastItem : ObservableObject
{
    private Action? _action;
    private readonly Action<ToastItem> _dismiss;
    public string Message { get; }
    public ToastLevel Level { get; }
    public string ActionLabel { get; }
    public bool HasAction => _action is not null;
    public string Title => Level switch { ToastLevel.Success=>"成功",ToastLevel.Warning=>"注意",ToastLevel.Error=>"错误",_=>"提示" };
    public string IconKind => Level switch { ToastLevel.Success=>"StatusCheck",ToastLevel.Warning=>"StatusWarning",ToastLevel.Error=>"StatusError",_=>"StatusInfo" };
    public bool IsSuccess => Level==ToastLevel.Success;
    public bool IsWarning => Level==ToastLevel.Warning;
    public bool IsError => Level==ToastLevel.Error;
    [ObservableProperty] private bool _isClosing;
    internal ToastItem(string message,ToastLevel level,Action? action,string actionLabel,Action<ToastItem> dismiss)
    { Message=message;Level=level;_action=action;ActionLabel=actionLabel;_dismiss=dismiss; }
    [RelayCommand] private void Close() => _dismiss(this);
    [RelayCommand] private void InvokeAction()
    {
        if(IsClosing)return;
        var action=_action;_action=null;OnPropertyChanged(nameof(HasAction));
        _dismiss(this);action?.Invoke();
    }
    internal void ForgetAction(){_action=null;OnPropertyChanged(nameof(HasAction));}
}

public sealed class ToastManager : IDisposable
{
    public const int MaxVisibleToasts=4;
    public ObservableCollection<ToastItem> Items {get;}=[];
    private readonly Dictionary<ToastItem,IDisposable> _timers=[];
    private bool _disposed;
    public event Action? Changed;
    public ToastManager() => MotionPreferences.Changed+=MotionChanged;
    public void Show(string message,ToastLevel level=ToastLevel.Info,Action? action=null,string actionLabel="查看",TimeSpan? duration=null)
    {
        if(!Dispatcher.UIThread.CheckAccess()){Dispatcher.UIThread.Post(()=>Show(message,level,action,actionLabel,duration));return;}
        if(_disposed || string.IsNullOrWhiteSpace(message))return;
        while(Items.Count>=MaxVisibleToasts)Remove(Items[0]);
        var item=new ToastItem(message,level,action,actionLabel,Dismiss);
        Items.Add(item);Changed?.Invoke();
        _timers[item]=DispatcherTimer.RunOnce(()=>Dismiss(item),duration??TimeSpan.FromSeconds(5));
    }
    public void Dismiss(ToastItem item)
    {
        if(!Items.Contains(item)||item.IsClosing)return;
        StopTimer(item);item.IsClosing=true;item.ForgetAction();Changed?.Invoke();
        if(!MotionPreferences.Enabled)Remove(item);
        else _timers[item]=DispatcherTimer.RunOnce(()=>Remove(item),MotionTokens.ToastExit);
    }
    private void StopTimer(ToastItem item){if(_timers.Remove(item,out var timer))timer.Dispose();}
    private void Remove(ToastItem item){StopTimer(item);item.ForgetAction();Items.Remove(item);Changed?.Invoke();}
    public void Clear(){foreach(var item in Items.ToArray())Remove(item);}
    private void MotionChanged(){if(!MotionPreferences.Enabled)foreach(var item in Items.Where(i=>i.IsClosing).ToArray())Remove(item);}
    public void Dispose(){if(_disposed)return;_disposed=true;MotionPreferences.Changed-=MotionChanged;Clear();}
}
