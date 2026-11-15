using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Launcher.App.ViewModels;

public sealed partial class MainViewModel
{
    public bool IsDevShowcaseEnabled {get;}=Environment.GetEnvironmentVariable("FANGYU_DEV_SHOWCASE")=="1";
    [ObservableProperty] private bool _isShowcaseOpen;
    public bool IsShowcasePage=>IsDevShowcaseEnabled && ShellPage=="showcase";
    partial void OnIsShowcaseOpenChanged(bool value)=>NotifyWorkspaceNavigation();
    private void InitializeShowcase()
    {
        BeforeMainNavigation+=()=>IsShowcaseOpen=false;
        PropertyChanged+=(_,e)=>
        {
            if(IsShowcaseOpen && (e.PropertyName==nameof(IsInstanceSection)
                || e.PropertyName==nameof(IsHomeSection)&&IsHomeSection
                || e.PropertyName==nameof(IsDetailOpen)&&IsDetailOpen
                || e.PropertyName==nameof(IsPreferencesOpen)&&IsPreferencesOpen
                || e.PropertyName==nameof(IsMultiplayerOpen)&&IsMultiplayerOpen
                || e.PropertyName==nameof(IsExperienceOpen)&&IsExperienceOpen))IsShowcaseOpen=false;
        };
    }
    [RelayCommand(CanExecute=nameof(IsDevShowcaseEnabled))]
    private void OpenShowcase()
    {
        if(!IsDevShowcaseEnabled)return;
        BeforeMainNavigation?.Invoke();IsHomeSection=false;IsShowcaseOpen=true;
    }
}
