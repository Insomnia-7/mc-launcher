using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Launcher.App.ViewModels;

namespace Launcher.Desktop.Views.Dev;

public sealed partial class UiShowcaseView : UserControl, IDisposable
{
    private CancellationTokenSource? _entrance;
    private Task _playing=Task.CompletedTask;
    private int _revision;
    public UiShowcaseViewModel? Model=>DataContext as UiShowcaseViewModel;
    public IReadOnlyList<Control> Stages=>[Stage1,Stage2,Stage3,Stage4,Stage5,Stage6,Stage7];
    public UiShowcaseView()
    {
        InitializeComponent();
        Loaded+=async(_,_)=>
        {
            Model?.Activate();
            ((IPseudoClasses)ShowcaseHover.Classes).Set(":pointerover",true);
            ((IPseudoClasses)ShowcasePressed.Classes).Set(":pressed",true);
            ((IPseudoClasses)ShowcaseFocused.Classes).Set(":focus-visible",true);
            await ReplayEntranceAsync();
        };
        Unloaded+=(_,_)=>{++_revision;_entrance?.Cancel();Model?.Deactivate();};
    }
    private async void ReplayEntrance(object? sender,RoutedEventArgs e)=>await ReplayEntranceAsync();
    public async Task ReplayEntranceAsync()
    {
        var revision=++_revision;_entrance?.Cancel();await _playing;
        if(revision!=_revision || !IsLoaded || Model?.CanPreview!=true)return;
        using var entrance=new CancellationTokenSource();_entrance=entrance;
        _playing=Motion.PlayStaggerSequenceAsync(Stages,entrance.Token);await _playing;
        if(ReferenceEquals(_entrance,entrance))_entrance=null;
    }
    public void Dispose(){++_revision;_entrance?.Cancel();Model?.Dispose();}
}
