using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Launcher.Desktop.Controls;

// Fabric artwork attribution and CC BY-NC-SA terms: docs/loader-illustrations.md.
/// <summary>Resolution-independent identifying illustrations; not a claim of project endorsement.</summary>
public sealed class LoaderIllustration : Control
{
    public static readonly StyledProperty<string> KindProperty = AvaloniaProperty.Register<LoaderIllustration,string>(nameof(Kind), "vanilla");
    public string Kind { get => GetValue(KindProperty); set => SetValue(KindProperty,value); }
    private sealed record Shape(Geometry Path, IBrush Fill, IPen? Stroke);
    private static Shape S(string path,string fill,string? stroke=null,double width=1.4) => new(Geometry.Parse(path),Brush.Parse(fill),stroke is null ? null : new Pen(Brush.Parse(stroke),width,lineCap:PenLineCap.Round,lineJoin:PenLineJoin.Round));
    private static readonly IReadOnlyDictionary<string,Shape[]> Marks = new Dictionary<string,Shape[]>
    {
        ["fabric"] = [
            S("M17 23L39 5L53 20L32 39Z", "#D4CBB2", "#756F61", 2),
            S("M13 27L35 9L49 24L28 43Z", "#E9E2CF", "#756F61", 2),
            S("M9 31L31 13L45 28L24 47Z", "#F7F1DF", "#756F61", 2),
            S("M9 31L24 47L20 57L5 41Z", "#C3B89C", "#756F61", 2),
            S("M24 47L45 28L42 38L20 57Z", "#B0A68D", "#756F61", 2),
            S("M12 33L17 38 M20 41L23 44 M28 20L31 23 M35 27L39 30", "Transparent", "#A79B7B", 1.5),
            S("M18 30L32 18 M23 35L37 23", "Transparent", "#D4C8AB", 1.1)],
        ["quilt"] = [
            S("M6 10L26 4L31 24L11 30Z", "#A02AF3"),
            S("M34 4L54 10L49 30L29 24Z", "#E03EC9"),
            S("M11 34L31 28L26 48L6 54Z", "#389FEF"),
            S("M34 29L54 35L48 55L28 49Z", "#5E55EA"),
            S("M11 14L23 10 M39 10L49 14 M11 47L20 43 M39 47L47 43", "Transparent", "#70FFFFFF", 1.5)],
        ["forge"] = [
            S("M4 15H57L50 23L38 26V35L46 43H19L25 35V26H17L6 22Z", "#263B50", "#15293C", 1.6),
            S("M4 15H57L50 19H9Z", "#58738B"),
            S("M26 26H37V35H26Z", "#344D63"),
            S("M19 43H46L50 51H15Z", "#1D3044"),
            S("M15 51H50V55H15Z", "#D9A566"),
            S("M49 5L47 11 M58 8L54 13 M39 5L40 10", "Transparent", "#E8B05D", 2)],
        ["neoforge"] = [
            S("M8 8L25 17H39L56 8L53 34L45 50L32 58L19 50L11 34Z", "#E57335", "#9A4928", 1.5),
            S("M11 12L22 19L13 26Z", "#56392E"),
            S("M53 12L42 19L51 26Z", "#56392E"),
            S("M16 26L27 31L32 46L20 44L13 33Z", "#FFF2D7"),
            S("M48 26L37 31L32 46L44 44L51 33Z", "#FFF2D7"),
            S("M21 29L26 32V36H21Z", "#302F35"),
            S("M38 32L43 29V36H38Z", "#302F35"),
            S("M26 42H38L32 48Z", "#302F35"),
            S("M22 48L32 52L42 48L32 58Z", "#FBE3BF")],
        ["vanilla"] = [
            S("M7 17L32 5L57 17L32 30Z", "#79B457"),
            S("M7 17L32 30V58L7 44Z", "#795339"),
            S("M32 30L57 17V44L32 58Z", "#9A6F46"),
            S("M7 17L32 30V38L25 34L20 36L15 31L7 28Z", "#4D8F41"),
            S("M32 30L57 17V28L50 30L46 37L40 35L32 40Z", "#65A24A"),
            S("M15 38L21 41V47L15 44Z M25 47L29 49V54L25 52Z", "#A47A4E"),
            S("M38 43L44 40V46L38 49Z M49 34L54 32V39L49 42Z", "#B68D5D")]
    };
    private static readonly IReadOnlyDictionary<Color,IBrush> DarkForge = new Dictionary<Color,IBrush>
    {
        [Color.Parse("#263B50")] = Brush.Parse("#A3B3CA"),
        [Color.Parse("#58738B")] = Brush.Parse("#D1DCEB"),
        [Color.Parse("#344D63")] = Brush.Parse("#8398B4"),
        [Color.Parse("#1D3044")] = Brush.Parse("#B1C0D5")
    };
    static LoaderIllustration() => AffectsRender<LoaderIllustration>(KindProperty);
    public LoaderIllustration() => ActualThemeVariantChanged += (_,_) => InvalidateVisual();
    public static bool HasMark(string kind) => Marks.ContainsKey(kind);
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var size=Math.Min(Bounds.Width,Bounds.Height);
        if(size<=0)return;
        var scale=size/64;
        using var transform=context.PushTransform(Matrix.CreateScale(scale,scale)*Matrix.CreateTranslation((Bounds.Width-size)/2,(Bounds.Height-size)/2));
        foreach(var shape in Marks.GetValueOrDefault(Kind,Marks["vanilla"]))
        {
            var fill=Kind=="forge" && ActualThemeVariant==ThemeVariant.Dark && shape.Fill is ISolidColorBrush color && DarkForge.TryGetValue(color.Color,out var dark) ? dark : shape.Fill;
            context.DrawGeometry(fill,shape.Stroke,shape.Path);
        }
    }
}
