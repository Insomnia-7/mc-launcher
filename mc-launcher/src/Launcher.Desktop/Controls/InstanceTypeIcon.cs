using System.Collections.Concurrent;
using System.Globalization;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Launcher.App.ViewModels;
using Launcher.Domain.Versions;

namespace Launcher.Desktop.Controls;

/// <summary>Shared, embedded loader marks. No network or per-card image decoding.</summary>
public sealed class InstanceTypeIcon : Control
{
    public static readonly StyledProperty<string> KindProperty = AvaloniaProperty.Register<InstanceTypeIcon,string>(nameof(Kind),"vanilla");
    public string Kind { get=>GetValue(KindProperty); set=>SetValue(KindProperty,value); }
    private static readonly ConcurrentDictionary<string,Lazy<Bitmap>> Bitmaps = new();
    private static readonly Lazy<(Geometry Shape,IBrush Fill)[]> Quilt = new(LoadQuilt);
    static InstanceTypeIcon() => AffectsRender<InstanceTypeIcon>(KindProperty);
    public InstanceTypeIcon()
    {
        RenderOptions.SetBitmapInterpolationMode(this,BitmapInterpolationMode.None);
        ActualThemeVariantChanged += (_,_) => InvalidateVisual();
    }
    public static string KeyFor(InstanceCard instance)
    {
        var loader=instance.LoaderName ?? VersionIdParser.Parse(instance.VersionId,instance.BaseVersion).Loader;
        if(!string.IsNullOrWhiteSpace(loader))
        {
            foreach(var key in new[]{"neoforge","fabric","quilt","forge","optifine"})
                if(loader.StartsWith(key,StringComparison.OrdinalIgnoreCase)) return key;
        }
        return "vanilla";
    }
    private static Bitmap Image(string name) => Bitmaps.GetOrAdd(name,key=>new Lazy<Bitmap>(()=>
    {
        using var stream=AssetLoader.Open(new Uri("avares://MCLauncher.Desktop/Assets/Loaders/"+key+".png"));
        return new Bitmap(stream);
    })).Value;
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if(Kind == "quilt")
        {
            const double size=913.53;
            var scale=Math.Min(Bounds.Width,Bounds.Height)/size;
            using var transform=context.PushTransform(Matrix.CreateScale(scale,scale)*Matrix.CreateTranslation((Bounds.Width-size*scale)/2,(Bounds.Height-size*scale)/2));
            foreach(var part in Quilt.Value) context.DrawGeometry(part.Fill,null,part.Shape);
            return;
        }
        if(Kind=="optifine")
        {
            var side=Math.Min(Bounds.Width,Bounds.Height); var center=new Point(Bounds.Width/2,Bounds.Height/2);
            context.DrawEllipse(Brush.Parse("#711B2B"),null,center,side/2,side/2);
            var text=new FormattedText("OF",CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface(FontFamily.Default,FontStyle.Normal,FontWeight.Bold),side*0.4,Brush.Parse("#F8D74D"));
            context.DrawText(text,new Point(center.X-text.Width/2,center.Y-text.Height/2)); return;
        }
        var forge=Kind=="forge";
        var image=Image(forge && ActualThemeVariant==ThemeVariant.Dark ? "forge-dark" : Kind is "forge" or "fabric" or "neoforge" ? Kind : "vanilla");
        // Forge publishes a wordmark; display only its original anvil, without altering the source asset.
        var source=forge?new Rect(2,14,76,40):new Rect(image.Size);
        var factor=Math.Min(Bounds.Width/source.Width,Bounds.Height/source.Height);
        var width=source.Width*factor;var height=source.Height*factor;
        context.DrawImage(image,source,new Rect((Bounds.Width-width)/2,(Bounds.Height-height)/2,width,height));
    }
    private static (Geometry,IBrush)[] LoadQuilt()
    {
        using var stream=AssetLoader.Open(new Uri("avares://MCLauncher.Desktop/Assets/Loaders/quilt.svg"));
        var document=XDocument.Load(stream); XNamespace svg="http://www.w3.org/2000/svg";
        var colors=new Dictionary<string,string>{{"cls-1","#9722ff"},{"cls-2","#dc29dd"},{"cls-3","#27a2fd"},{"cls-4","#3344ff"}};
        var shapes=document.Descendants(svg+"path").Select(p=>(Geometry.Parse(p.Attribute("d")!.Value),(IBrush)Brush.Parse(colors[p.Attribute("class")!.Value]))).ToList();
        var diamond=new RectangleGeometry(new Rect(635.3,635.29,235.14,235.14),13.53,13.53)
        {Transform=new MatrixTransform(Matrix.CreateRotation(-Math.PI/4)*Matrix.CreateTranslation(-311.85,752.86))};
        shapes.Add((diamond,Brush.Parse("#9722ff"))); return shapes.ToArray();
    }
}
