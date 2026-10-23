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
        image.Render(page);
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
            PageTransition.Play(page, 24, 340);
            snapshot.Opacity = 0;
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(210)) { FillBehavior = FillBehavior.Stop };
            fade.Completed += (_, _) => { if (_revision == revision) Clear(); };
            snapshot.BeginAnimation(UIElement.OpacityProperty, fade);
            motion.Translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -10, TimeSpan.FromMilliseconds(210)) { FillBehavior = FillBehavior.Stop });
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
