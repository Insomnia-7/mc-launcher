using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;

namespace Launcher.Desktop.Controls;

public enum FeedbackState { None, Loading, Success, Error }
/// <summary>Only command execution implies loading; success/error are supplied by business state.</summary>
public static class ButtonFeedback
{
    public static readonly AttachedProperty<bool> TrackCommandProperty=AvaloniaProperty.RegisterAttached<Button,bool>("TrackCommand",typeof(ButtonFeedback));
    public static readonly AttachedProperty<FeedbackState> StateProperty=AvaloniaProperty.RegisterAttached<Button,FeedbackState>("State",typeof(ButtonFeedback));
    private static readonly ConditionalWeakTable<Button,Subscription> Subscriptions=new();
    public static bool GetTrackCommand(Button button)=>button.GetValue(TrackCommandProperty);
    public static void SetTrackCommand(Button button,bool value)=>button.SetValue(TrackCommandProperty,value);
    public static FeedbackState GetState(Button button)=>button.GetValue(StateProperty);
    public static void SetState(Button button,FeedbackState value)=>button.SetValue(StateProperty,value);
    static ButtonFeedback()=>TrackCommandProperty.Changed.AddClassHandler<Button>((button,_)=>
    {
        if(GetTrackCommand(button))Subscriptions.GetValue(button,b=>new Subscription(b));
        else if(Subscriptions.TryGetValue(button,out var state)){state.Dispose();Subscriptions.Remove(button);}
    });
    private sealed class Subscription : IDisposable
    {
        private readonly Button _button;
        private IAsyncRelayCommand? _command;
        public Subscription(Button button)
        {
            _button=button;button.AttachedToVisualTree+=Attached;button.DetachedFromVisualTree+=Detached;button.PropertyChanged+=PropertyChanged;
            if(TopLevel.GetTopLevel(button) is not null)BindCommand();
        }
        private void Attached(object? sender,VisualTreeAttachmentEventArgs e)=>BindCommand();
        private void Detached(object? sender,VisualTreeAttachmentEventArgs e)=>Unbind();
        private void PropertyChanged(object? sender,AvaloniaPropertyChangedEventArgs e){if(e.Property==Button.CommandProperty && TopLevel.GetTopLevel(_button) is not null)BindCommand();}
        private void BindCommand(){Unbind();_command=_button.Command as IAsyncRelayCommand;if(_command is not null)_command.PropertyChanged+=CommandChanged;Refresh();}
        private void Unbind(){if(_command is not null)_command.PropertyChanged-=CommandChanged;_command=null;_button.Classes.Set("is-loading",false);}
        private void CommandChanged(object? sender,PropertyChangedEventArgs e){if(e.PropertyName==nameof(IAsyncRelayCommand.IsRunning))Refresh();}
        private void Refresh()=>_button.Classes.Set("is-loading",_command?.IsRunning==true);
        public void Dispose(){Unbind();_button.AttachedToVisualTree-=Attached;_button.DetachedFromVisualTree-=Detached;_button.PropertyChanged-=PropertyChanged;}
    }
}
