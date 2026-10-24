using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Launcher.Desktop.Controls;

// A small retained drawing: no image download, video, timer, or continuous animation.
public sealed class WorldArt : Control
{
    private static readonly IBrush Grass = Brush.Parse("#A9D8BA"), Left = Brush.Parse("#6F9E82"), Right = Brush.Parse("#497968");
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        using var scale = context.PushTransform(Matrix.CreateScale(Bounds.Width / 360, Bounds.Height / 280));
        context.DrawEllipse(Brush.Parse("#124CAF95"), null, new Point(185, 228), 112, 25);
        for (var sum = 0; sum <= 4; sum++)
            for (var i = 0; i < 3; i++)
            {
                var j = sum - i;
                if (j is < 0 or > 2) continue;
                Cube(context, 150 + (i - j) * 31, 94 + (i + j) * 15.5, 31, 50, Grass, Left, Right);
            }
        Cube(context, 160, 68, 9, 39, Brush.Parse("#B5C3A0"), Brush.Parse("#6F8066"), Brush.Parse("#506B57"));
        Cube(context, 131, 29, 29, 28, Brush.Parse("#B8E2CD"), Brush.Parse("#76AC93"), Brush.Parse("#4F8B74"));
        Cube(context, 150, 14, 24, 24, Brush.Parse("#D0ECDA"), Brush.Parse("#91C7A7"), Brush.Parse("#6BA88C"));
        Cube(context, 225, 126, 13, 16, Brush.Parse("#E2DCC6"), Brush.Parse("#B1B29F"), Brush.Parse("#8C9D8D"));
        Cube(context, 83, 192, 13, 18, Grass, Left, Right);
        Cube(context, 270, 79, 8, 11, Grass, Left, Right);
        context.DrawEllipse(Brush.Parse("#55B4D8D0"), null, new Point(292, 173), 3, 3);
        context.DrawEllipse(Brush.Parse("#5592B8AB"), null, new Point(77, 70), 2, 2);
    }
    private static void Cube(DrawingContext c, double x, double y, double size, double height, IBrush top, IBrush left, IBrush right)
    {
        var a = new Point(x, y); var b = new Point(x + size, y - size / 2); var d = new Point(x + size, y + size / 2); var e = new Point(x + size * 2, y);
        Polygon(c, left, a, d, d + new Vector(0, height), a + new Vector(0, height));
        Polygon(c, right, d, e, e + new Vector(0, height), d + new Vector(0, height));
        Polygon(c, top, a, b, e, d);
    }
    private static void Polygon(DrawingContext c, IBrush brush, params Point[] points)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open()) { g.BeginFigure(points[0], true); foreach (var p in points.Skip(1)) g.LineTo(p); g.EndFigure(true); }
        c.DrawGeometry(brush, new Pen(Brush.Parse("#10FFFFFF"), 0.7), geometry);
    }
}
