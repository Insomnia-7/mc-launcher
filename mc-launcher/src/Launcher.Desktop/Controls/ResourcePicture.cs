using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Launcher.Desktop.Controls;

/// <summary>A bounded, disposable landscape decoder; project screenshots keep their original aspect ratio.</summary>
public sealed class ResourcePicture : Control
{
    public static readonly StyledProperty<byte[]?> BytesProperty = AvaloniaProperty.Register<ResourcePicture, byte[]?>(nameof(Bytes));
    public byte[]? Bytes { get => GetValue(BytesProperty); set => SetValue(BytesProperty, value); }
    private Bitmap? _bitmap;
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    { base.OnPropertyChanged(change); if (change.Property == BytesProperty) Decode(); }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); Decode(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { _bitmap?.Dispose(); _bitmap = null; base.OnDetachedFromVisualTree(e); }
    private void Decode()
    {
        _bitmap?.Dispose(); _bitmap = null;
        if (Bytes is { Length: > 0 } bytes)
            try { using var stream = new MemoryStream(bytes); _bitmap = Bitmap.DecodeToWidth(stream, 1280); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or IOException or NullReferenceException) { }
        InvalidateVisual();
    }
    public override void Render(DrawingContext context)
    {
        if (_bitmap is null || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var scale = Math.Max(Bounds.Width / _bitmap.Size.Width, Bounds.Height / _bitmap.Size.Height);
        var width = Bounds.Width / scale; var height = Bounds.Height / scale;
        context.DrawImage(_bitmap, new Rect((_bitmap.Size.Width - width) / 2, (_bitmap.Size.Height - height) / 2, width, height), new Rect(Bounds.Size));
    }
}
