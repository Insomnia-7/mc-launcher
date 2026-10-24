using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace Launcher.Desktop.Controls;

public sealed class UiIcon : Control
{
    public static readonly StyledProperty<string?> KindProperty = AvaloniaProperty.Register<UiIcon,string?>(nameof(Kind));
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TemplatedControl.ForegroundProperty.AddOwner<UiIcon>();
    public string? Kind { get => GetValue(KindProperty); set => SetValue(KindProperty,value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty,value); }
    private sealed record Part(Geometry Geometry, IBrush? Fill, IBrush? Stroke, double Width, bool FollowForeground);
    private static readonly ConcurrentDictionary<string,Part[]> Parsed = new();
    static UiIcon() => AffectsRender<UiIcon>(KindProperty,ForegroundProperty);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if(Kind is null || !IconCatalog.Items.TryGetValue(Kind,out var definition)) return;
        var parts=Parsed.GetOrAdd(Kind,_=>definition.Parts.Select(p=>new Part(Geometry.Parse(p.Path),ParseBrush(p.Fill,p.Opacity),ParseBrush(p.Stroke,p.Opacity),p.Width,definition.FollowForeground)).ToArray());
        var scale=Math.Min(Bounds.Width,Bounds.Height)/definition.Size;
        if(scale<=0) return;
        using var transform=context.PushTransform(Matrix.CreateScale(scale,scale)*Matrix.CreateTranslation((Bounds.Width-definition.Size*scale)/2,(Bounds.Height-definition.Size*scale)/2));
        foreach(var part in parts)
        {
            // Line icons keep a legible physical stroke at small toolbar sizes.
            var stroke=part.FollowForeground ? Foreground : part.Stroke;
            var pen=stroke is null ? null : new Pen(stroke,part.FollowForeground ? 1.65/scale : part.Width,lineCap:PenLineCap.Round,lineJoin:PenLineJoin.Round);
            context.DrawGeometry(part.FollowForeground && Kind=="ActionPlay" ? Foreground : part.Fill,pen,part.Geometry);
        }
    }
    private static IBrush? ParseBrush(string? value,double opacity)
    {
        if(value is null) return null;
        var color=Color.Parse(value);
        // Apply alpha to the paint instead of creating an offscreen opacity layer.
        return new SolidColorBrush(Color.FromArgb((byte)Math.Round(color.A*opacity),color.R,color.G,color.B));
    }
}
