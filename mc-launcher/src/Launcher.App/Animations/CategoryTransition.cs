using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Launcher.App.Animations;

/// <summary>Capture only on category changes, before bindings replace the old content.</summary>
public sealed class CategoryTransition(FrameworkElement page, Image snapshot)
{
    private int _revision;

    public void Play()
    {
        Clear();
        if (!MotionPreferences.Enabled || !page.IsVisible || page.ActualWidth < 1 || page.ActualHeight < 1) return;
        var revision = _revision;
        var ratio = Math.Min(1, 1920 / page.ActualWidth);
        var image = new RenderTargetBitmap((int)Math.Ceiling(page.ActualWidth * ratio), (int)Math.Ceiling(page.ActualHeight * ratio), 96 * ratio, 96 * ratio, PixelFormats.Pbgra32);
        // 页面此刻可能还挂着上一段切页动画的 Translate/Scale，直接 Render 会把它们烘进快照，
        // 贴回页面上就变成「旧界面往右下偏」。截图前把自身变换归零，截完再还原。
        var pageMotion=MotionCore.TryGet(page);
        var savedY=0d; var savedScale=1d;
        if(pageMotion is not null) {
            savedY=pageMotion.Translate.Y; savedScale=pageMotion.Scale.ScaleX;
            pageMotion.Translate.BeginAnimation(TranslateTransform.YProperty,null); pageMotion.Translate.Y=0;
            pageMotion.Scale.BeginAnimation(ScaleTransform.ScaleXProperty,null);
            pageMotion.Scale.BeginAnimation(ScaleTransform.ScaleYProperty,null);
            pageMotion.Scale.ScaleX=pageMotion.Scale.ScaleY=1;
            page.UpdateLayout();
        }
        image.Render(page);
        if(pageMotion is not null) {
            pageMotion.Translate.Y=savedY;
            pageMotion.Scale.ScaleX=pageMotion.Scale.ScaleY=savedScale;
        }
        image.Freeze();
        snapshot.Source = image;
        snapshot.Opacity = 1;
        snapshot.Visibility = Visibility.Visible;
        var motion = MotionCore.Ensure(snapshot);
        motion.Translate.Y = 0;
        page.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (_revision != revision) return;
            if (!MotionPreferences.Enabled) { Clear(); return; }
            PageTransition.Play(page, 18, 300);
            snapshot.Opacity = 0;
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300)) { FillBehavior = FillBehavior.Stop };
            fade.Completed += (_, _) => { if (_revision == revision) Clear(); };
            snapshot.BeginAnimation(UIElement.OpacityProperty, fade);
            motion.Translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -8, TimeSpan.FromMilliseconds(300)) { FillBehavior = FillBehavior.Stop });
        }));
    }

    public void Clear()
    {
        ++_revision;
        snapshot.BeginAnimation(UIElement.OpacityProperty, null);
        if (MotionCore.TryGet(snapshot) is { } motion) motion.Translate.BeginAnimation(TranslateTransform.YProperty, null);
        snapshot.Visibility = Visibility.Collapsed;
        snapshot.Source = null;
    }
}
