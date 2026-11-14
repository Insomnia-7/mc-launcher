using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media.Transformation;
using Avalonia.VisualTree;

namespace Launcher.Desktop.Controls;

/// <summary>
/// 分段控件的滑动高亮。尺寸与位置瞬时落位，位移只走 RenderTransform（大纲 §6：布局动画只许侧边栏一处）。
/// </summary>
public sealed class SelectionHighlight:Border
{
    private bool? _animated;
    private Button? _target;

    public void Follow(ItemsControl host)
    {
        var button=host.GetVisualDescendants().OfType<Button>().FirstOrDefault(b=>b.IsEffectivelyVisible&&b.Classes.Contains("active"));
        if(button?.TranslatePoint(new Point(),Parent as Visual??host) is not {} position)return;
        var animated=MotionPreferences.Enabled;
        if(_animated!=animated)
        {
            _animated=animated;
            Transitions=animated?new Transitions
            {
                new TransformOperationsTransition{Property=RenderTransformProperty,Duration=MotionTiming.Normal,Easing=MotionEasing.Entrance}
            }:null;
        }
        var moved=_target is not null&&!ReferenceEquals(_target,button);
        var target=new Thickness(position.X,position.Y,0,0);
        if(moved&&animated&&Width>0)
        {
            var dx=Margin.Left-target.Left;var dy=Margin.Top-target.Top;
            Transitions=null;
            RenderTransform=dx==0&&dy==0
                ?TransformOperations.Identity
                :TransformOperations.Parse($"translate({dx.ToString(System.Globalization.CultureInfo.InvariantCulture)}px,{dy.ToString(System.Globalization.CultureInfo.InvariantCulture)}px)");
            Width=button.Bounds.Width;Height=button.Bounds.Height;Margin=target;
            Transitions=new Transitions{new TransformOperationsTransition{Property=RenderTransformProperty,Duration=MotionTiming.Normal,Easing=MotionEasing.Entrance}};
            RenderTransform=TransformOperations.Identity;
        }
        else
        {
            Width=button.Bounds.Width;Height=button.Bounds.Height;Margin=target;
            RenderTransform=TransformOperations.Identity;
        }
        _target=button;
    }
}
