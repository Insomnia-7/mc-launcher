using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.Application.Operations;

namespace Launcher.App.ViewModels;
public sealed partial class MainViewModel
{
    private DispatcherTimer? _noticeTimer;
    private Action? _noticeAction;
    private readonly Dictionary<string,DurableTaskState> _noticeTaskStates=new();
    [ObservableProperty] private string _noticeText="";
    public bool HasNotice=>NoticeText.Length>0;
    public bool HasNoticeAction=>_noticeAction is not null;
    partial void OnNoticeTextChanged(string value)=>OnPropertyChanged(nameof(HasNotice));
    public void ShowNotice(string message,Action? action=null)
    {
        _noticeAction=action;NoticeText=message;OnPropertyChanged(nameof(HasNoticeAction));
        _noticeTimer??=new DispatcherTimer(TimeSpan.FromSeconds(6),DispatcherPriority.Background,(_,_)=>DismissNotice());
        _noticeTimer.Stop();_noticeTimer.Start();
    }
    [RelayCommand] private void DismissNotice(){_noticeTimer?.Stop();NoticeText="";_noticeAction=null;OnPropertyChanged(nameof(HasNoticeAction));}
    [RelayCommand] private void OpenNotice(){var action=_noticeAction;DismissNotice();action?.Invoke();}
    private void NotifyTaskFeedback(DurableTaskRecord task)
    {
        if(!_noticeTaskStates.TryGetValue(task.Id,out var previous))previous=DurableTaskState.Ready;
        _noticeTaskStates[task.Id]=task.State;
        if(previous==task.State)return;
        if(task.State==DurableTaskState.Completed)ShowNotice(task.Title+" · 已完成",()=>OpenTaskCenter());
        else if(task.State==DurableTaskState.Failed)ShowNotice(task.Title+" · 未完成，点击查看原因",()=>OpenTaskCenter());
    }
}
