using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Launcher.App.ViewModels;
using Launcher.Desktop.Controls;

namespace Launcher.Desktop.Views;
public sealed partial class ToastView : UserControl
{
    private ToastItem? _item;
    public ToastView()=>InitializeComponent();
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);_item=DataContext as ToastItem;
        if(_item is not null)_item.PropertyChanged+=ItemChanged;
        MotionPreferences.Changed+=PreferenceChanged;
        if(MotionPreferences.Enabled && _item?.IsClosing!=true)AnimatedContentHost.Move(this,0,1,8,0,MotionTokens.TextFade,MotionEasing.Entrance);else Normalize();
    }
    private void ItemChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(e.PropertyName==nameof(ToastItem.IsClosing) && _item?.IsClosing==true)
        {
            IsHitTestVisible=false;
            if(MotionPreferences.Enabled)AnimatedContentHost.Move(this,Opacity,0,RenderTransform?.Value.M32??0,6,MotionTokens.ToastExit,MotionEasing.Exit);
            else Normalize();
        }
    }
    private void PreferenceChanged(){if(!MotionPreferences.Enabled)Normalize();}
    private void Normalize(){Transitions=null;RenderTransform=new TranslateTransform();Opacity=_item?.IsClosing==true?0:1;}
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if(_item is not null)_item.PropertyChanged-=ItemChanged;
        MotionPreferences.Changed-=PreferenceChanged;_item=null;Normalize();base.OnDetachedFromVisualTree(e);
    }
}
