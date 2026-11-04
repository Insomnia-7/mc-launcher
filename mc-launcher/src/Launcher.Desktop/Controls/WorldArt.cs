using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Launcher.Desktop.Controls;

/// <summary>One shared decoded texture illustration; no timer, network or repeated decoding.</summary>
public sealed class WorldArt : Control
{
    private static readonly Lazy<Bitmap> Art = new(() =>
    {
        using var stream = AssetLoader.Open(new Uri("avares://MCLauncher.Desktop/Assets/Brand/world-meadow-v2.png"));
        return Bitmap.DecodeToWidth(stream, 640);
    });
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bitmap = Art.Value;
        var scale = Math.Min(Bounds.Width / bitmap.Size.Width, Bounds.Height / bitmap.Size.Height);
        var width = bitmap.Size.Width * scale; var height = bitmap.Size.Height * scale;
        context.DrawImage(bitmap, new Rect(bitmap.Size), new Rect((Bounds.Width-width)/2,(Bounds.Height-height)/2,width,height));
    }
}
