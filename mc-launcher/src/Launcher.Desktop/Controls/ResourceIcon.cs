using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Launcher.Desktop.Controls;

/// <summary>Owns the small decoded bitmap; releases it when a resource row leaves the visual tree.</summary>
public sealed class ResourceIcon : Control
{
    public static readonly StyledProperty<byte[]?> BytesProperty=AvaloniaProperty.Register<ResourceIcon,byte[]?>(nameof(Bytes));
    public byte[]? Bytes { get=>GetValue(BytesProperty); set=>SetValue(BytesProperty,value); }
    private Bitmap? _bitmap;
    static ResourceIcon()=>AffectsRender<ResourceIcon>(BytesProperty);
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if(change.Property==BytesProperty) Decode();
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); Decode(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { _bitmap?.Dispose(); _bitmap=null; base.OnDetachedFromVisualTree(e); }
    private void Decode()
    {
        _bitmap?.Dispose(); _bitmap=null;
        if(Bytes is not {Length:>0} data) return;
        try { using var stream=new MemoryStream(data); _bitmap=Bitmap.DecodeToWidth(stream,96); }
        catch(Exception ex) when(ex is ArgumentException or InvalidOperationException or NotSupportedException or IOException) { }
        InvalidateVisual();
    }
    public override void Render(DrawingContext context)
    {
        if(_bitmap is not null) context.DrawImage(_bitmap,new Rect(_bitmap.Size),new Rect(Bounds.Size));
    }
}
