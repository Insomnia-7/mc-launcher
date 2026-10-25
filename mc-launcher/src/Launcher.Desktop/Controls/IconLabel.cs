using Avalonia;
using Avalonia.Controls.Primitives;

namespace Launcher.Desktop.Controls;

public sealed class IconLabel : TemplatedControl
{
    public static readonly StyledProperty<string?> IconProperty = AvaloniaProperty.Register<IconLabel,string?>(nameof(Icon));
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<IconLabel,string?>(nameof(Text));
    public static readonly StyledProperty<double> IconSizeProperty = AvaloniaProperty.Register<IconLabel,double>(nameof(IconSize),22);
    public string? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty,value); }
    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty,value); }
    public double IconSize { get => GetValue(IconSizeProperty); set => SetValue(IconSizeProperty,value); }
    public override string ToString() => Text ?? "";
}
