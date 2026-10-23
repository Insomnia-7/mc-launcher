using System.Windows;
using System.Windows.Media.Animation;

namespace Launcher.App.Animations;

/// <summary>进入淡入并落位，退出完成后再折叠；连续切换只保留最后一次状态。</summary>
public static class PageVisibility
{
    public static readonly DependencyProperty IsActiveProperty=DependencyProperty.RegisterAttached("IsActive",typeof(bool),typeof(PageVisibility),new PropertyMetadata(false,OnActive));
    public static bool GetIsActive(DependencyObject value)=>(bool)value.GetValue(IsActiveProperty);
    public static void SetIsActive(DependencyObject value,bool active)=>value.SetValue(IsActiveProperty,active);
    public static readonly DependencyProperty TransitionKeyProperty=DependencyProperty.RegisterAttached("TransitionKey",typeof(object),typeof(PageVisibility),new PropertyMetadata(null,OnKey));
    public static object GetTransitionKey(DependencyObject value)=>value.GetValue(TransitionKeyProperty);
    public static void SetTransitionKey(DependencyObject value,object key)=>value.SetValue(TransitionKeyProperty,key);
    private static void OnKey(DependencyObject target,DependencyPropertyChangedEventArgs e)
    {
        if(target is FrameworkElement element && element.IsLoaded && GetIsActive(element) && SystemParameters.ClientAreaAnimation) PageTransition.Play(element,6,160);
    }
    private static void OnActive(DependencyObject target,DependencyPropertyChangedEventArgs e)
    {
        if(target is not FrameworkElement element) return;
        var active=(bool)e.NewValue;
        var state=MotionCore.Ensure(element); var token=++state.Token;
        element.IsHitTestVisible=active;
        if(!element.IsLoaded || !SystemParameters.ClientAreaAnimation) {
            element.BeginAnimation(UIElement.OpacityProperty,null); element.Opacity=1;
            state.Translate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,null); state.Translate.Y=0;
            element.Visibility=active?Visibility.Visible:Visibility.Collapsed; return;
        }
        if(active) { element.Visibility=Visibility.Visible; PageTransition.Play(element,10,190); return; }
        var fade=new DoubleAnimation(element.Opacity,0,TimeSpan.FromMilliseconds(110)) { EasingFunction=new QuadraticEase { EasingMode=EasingMode.EaseIn },FillBehavior=FillBehavior.Stop };
        element.Opacity=0;
        fade.Completed+=(_,_)=> {
            if(state.Token!=token || GetIsActive(element)) return;
            element.Visibility=Visibility.Collapsed; element.BeginAnimation(UIElement.OpacityProperty,null); element.Opacity=1;
        };
        element.BeginAnimation(UIElement.OpacityProperty,fade,HandoffBehavior.SnapshotAndReplace);
    }
}
