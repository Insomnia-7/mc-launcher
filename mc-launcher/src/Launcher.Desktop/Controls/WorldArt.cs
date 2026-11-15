using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Launcher.Desktop.Controls;

/// <summary>Original, static block landscape for the A workbench. No image decode or animation loop.</summary>
public sealed class WorldArt : Control
{
    private static IBrush Ink(string value) => new SolidColorBrush(Color.Parse(value));
    private static readonly IBrush Sky = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0,0,RelativeUnit.Relative), EndPoint = new RelativePoint(1,1,RelativeUnit.Relative),
        GradientStops = [new(Color.Parse("#282A50"),0),new(Color.Parse("#8071A9"),0.6),new(Color.Parse("#E8BBAF"),1)]
    };
    private static readonly IBrush Distant = Ink("#625A87"), Mid = Ink("#454862"), Ground = Ink("#303A49");
    private static readonly IBrush Grass = Ink("#738C84"), Side = Ink("#3F565D"), Front = Ink("#526B70"), Tree = Ink("#3A5B60"), Sun = Ink("#EFCEB7");
    private static StreamGeometry Polygon(params Point[] points)
    {
        var geometry = new StreamGeometry(); using var path = geometry.Open();
        path.BeginFigure(points[0],true); foreach(var point in points.Skip(1)) path.LineTo(point); path.EndFigure(true); return geometry;
    }
    private static readonly Geometry FarShape = Polygon(new(0,116),new(26,116),new(26,92),new(62,92),new(62,76),new(104,76),new(104,102),new(146,102),new(146,85),new(176,85),new(176,60),new(213,60),new(213,82),new(247,82),new(247,104),new(280,104),new(280,78),new(320,78),new(320,190),new(0,190));
    private static readonly Geometry MidShape = Polygon(new(0,146),new(35,146),new(35,125),new(75,125),new(75,112),new(111,112),new(111,128),new(156,128),new(156,118),new(204,118),new(204,134),new(246,134),new(246,114),new(283,114),new(283,123),new(320,123),new(320,190),new(0,190));
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var scale = Math.Min(Bounds.Width/320,Bounds.Height/190); if(scale<=0)return;
        using var transform = context.PushTransform(Matrix.CreateScale(scale,scale)*Matrix.CreateTranslation((Bounds.Width-320*scale)/2,(Bounds.Height-190*scale)/2));
        using var clip = context.PushClip(new RoundedRect(new Rect(0,0,320,190),12));
        context.DrawRectangle(Sky,null,new Rect(0,0,320,190));
        context.DrawEllipse(Sun,null,new Point(245,46),18,18);
        context.DrawGeometry(Distant,null,FarShape);context.DrawGeometry(Mid,null,MidShape);
        context.DrawRectangle(Ground,null,new Rect(0,166,320,24));
        Block(context,115,144,38); Block(context,169,156,31); Block(context,218,146,38);
        context.DrawRectangle(Side,null,new Rect(56,124,7,43));
        context.DrawRectangle(Tree,null,new Rect(37,96,44,34));
        context.DrawRectangle(Tree,null,new Rect(44,80,30,28));
        context.DrawRectangle(Grass,null,new Rect(234,135,17,3));
    }
    private static void Block(DrawingContext context,double x,double y,double size)
    {
        context.DrawGeometry(Grass,null,Polygon(new(x,y-size/2),new(x+size,y),new(x,y+size/2),new(x-size,y)));
        context.DrawGeometry(Front,null,Polygon(new(x-size,y),new(x,y+size/2),new(x,y+size),new(x-size,y+size/2)));
        context.DrawGeometry(Side,null,Polygon(new(x+size,y),new(x,y+size/2),new(x,y+size),new(x+size,y+size/2)));
    }
}
