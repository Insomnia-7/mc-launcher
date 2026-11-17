using CommunityToolkit.Mvvm.Input;
using Launcher.Application.Operations;

namespace Launcher.App.ViewModels;
public sealed partial class MainViewModel
{
    private readonly Dictionary<string,DurableTaskState> _noticeTaskStates=new();
    public ToastManager Toasts {get;}=new();
    private ToastItem? LatestNotice=>Toasts.Items.LastOrDefault(i=>!i.IsClosing);
    public string NoticeText=>LatestNotice?.Message??"";
    public bool HasNotice=>LatestNotice is not null;
    public bool HasNoticeAction=>LatestNotice?.HasAction==true;
    private void InitializeNotices()=>Toasts.Changed+=()=>
    {OnPropertyChanged(nameof(NoticeText));OnPropertyChanged(nameof(HasNotice));OnPropertyChanged(nameof(HasNoticeAction));};
    public void ShowNotice(string message,Action? action=null,ToastLevel level=ToastLevel.Info)=>Toasts.Show(message,level,action);
    [RelayCommand] private void DismissNotice()=>Toasts.Clear();
    [RelayCommand] private void OpenNotice(){var item=LatestNotice;item?.InvokeActionCommand.Execute(null);Toasts.Clear();}
    private void NotifyTaskFeedback(DurableTaskRecord task)
    {
        if(!_noticeTaskStates.TryGetValue(task.Id,out var previous))previous=DurableTaskState.Ready;
        _noticeTaskStates[task.Id]=task.State;
        if(previous==task.State)return;
        if(task.State==DurableTaskState.Completed&&task.Request.Arguments.TryGetValue("quiet",out var quiet)&&quiet=="true")return;
        if(task.State==DurableTaskState.Completed)ShowNotice(task.Title+" · 已完成",()=>OpenTaskCenter(),ToastLevel.Success);
        else if(task.State==DurableTaskState.Failed)ShowNotice(task.Title+" · 未完成，点击查看原因",()=>OpenTaskCenter(),ToastLevel.Error);
    }
}
