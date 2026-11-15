using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;

namespace Launcher.Desktop.Controls;

public static class PopupMotion
{
    public static readonly AttachedProperty<bool> EnabledProperty=AvaloniaProperty.RegisterAttached<ToolTip,bool>("Enabled",typeof(PopupMotion));
    private static readonly ConditionalWeakTable<ToolTip,State> States=new();
    public static bool GetEnabled(ToolTip tip)=>tip.GetValue(EnabledProperty);
    public static void SetEnabled(ToolTip tip,bool value)=>tip.SetValue(EnabledProperty,value);
    static PopupMotion()=>EnabledProperty.Changed.AddClassHandler<ToolTip>((tip,_)=>
    {
        if(GetEnabled(tip))States.GetValue(tip,t=>new State(t));
        else if(States.TryGetValue(tip,out var state)){state.Dispose();States.Remove(tip);}
    });
    private sealed class State : IDisposable
    {
        private readonly ToolTip _tip;
        public State(ToolTip tip){_tip=tip;tip.AttachedToVisualTree+=Attached;tip.DetachedFromVisualTree+=Detached;if(TopLevel.GetTopLevel(tip) is not null)Enter();}
        private void Attached(object? sender,VisualTreeAttachmentEventArgs e)=>Enter();
        private void Enter(){MotionPreferences.Changed-=PreferenceChanged;MotionPreferences.Changed+=PreferenceChanged;if(MotionPreferences.Enabled)AnimatedContentHost.Move(_tip,0,1,4,0,MotionTokens.Tooltip,MotionEasing.Entrance);else Normalize();}
        private void PreferenceChanged(){if(!MotionPreferences.Enabled)Normalize();}
        private void Normalize(){_tip.Transitions=null;_tip.RenderTransform=null;_tip.Opacity=1;}
        private void Detached(object? sender,VisualTreeAttachmentEventArgs e){MotionPreferences.Changed-=PreferenceChanged;Normalize();}
        public void Dispose(){_tip.AttachedToVisualTree-=Attached;_tip.DetachedFromVisualTree-=Detached;MotionPreferences.Changed-=PreferenceChanged;Normalize();}
    }
}
