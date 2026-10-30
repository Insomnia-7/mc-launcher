using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Launcher.Desktop.Controls;

public sealed class SelectionHighlight:Border
{
    private bool? _animated;
    public void Follow(ItemsControl host)
    {
        var button=host.GetVisualDescendants().OfType<Button>().FirstOrDefault(b=>b.IsEffectivelyVisible&&b.Classes.Contains("active"));
        if(button?.TranslatePoint(new Point(),Parent as Visual??host) is not {} position)return;
        var animated=MotionPreferences.Enabled;
        if(_animated!=animated)
        {
            _animated=animated;Transitions=animated?new Transitions
            {
                new ThicknessTransition{Property=MarginProperty,Duration=TimeSpan.FromMilliseconds(180),Easing=new CubicEaseOut()},
                new DoubleTransition{Property=WidthProperty,Duration=TimeSpan.FromMilliseconds(180),Easing=new CubicEaseOut()},
                new DoubleTransition{Property=HeightProperty,Duration=TimeSpan.FromMilliseconds(180),Easing=new CubicEaseOut()}
            }:null;
        }
        Width=button.Bounds.Width;Height=button.Bounds.Height;Margin=new Thickness(position.X,position.Y,0,0);
    }
}
