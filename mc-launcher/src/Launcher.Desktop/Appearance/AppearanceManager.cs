using System.IO;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Launcher.Application.Settings;
using BitmapSource = Avalonia.Media.Imaging.Bitmap;

namespace Launcher.App.Appearance;

public sealed class AppearanceManager : IDisposable
{
    private readonly string _directory;
    private readonly IResourceDictionary _resources;
    private readonly Dictionary<string, SolidColorBrush> _brushes = new(StringComparer.Ordinal);
    private IPlatformSettings? _platformSettings;
    private bool _disposed;

    public string DataDirectory => _directory;
    public string ImageDirectory => Path.Combine(_directory, "appearance-data");
    public bool IsEffectiveLight { get; private set; }
    public event Action? SystemThemeChanged;

    public AppearanceManager(string directory, IResourceDictionary resources)
    {
        _directory = directory;
        _resources = resources;
        _platformSettings = Avalonia.Application.Current?.PlatformSettings;
        if (_platformSettings is not null)
            _platformSettings.ColorValuesChanged += OnPlatformColorValuesChanged;
    }

    public AppearanceSettings Read() => AppearanceStore.Read(_directory);

    public bool IsSystemLight() =>
        _platformSettings?.GetColorValues().ThemeVariant == PlatformThemeVariant.Light;

    private void OnPlatformColorValuesChanged(object? sender, PlatformColorValues e) =>
        Dispatcher.UIThread.Post(() => SystemThemeChanged?.Invoke());

    public void Apply(ThemeDefinition theme) => Apply(theme, animate: true);

    public void Apply(ThemeDefinition theme, bool animate)
    {
        var colors = ThemeCatalog.Resolve(theme);
        var duration = animate && Launcher.Desktop.MotionPreferences.Enabled
            ? Launcher.Desktop.MotionTiming.Theme
            : TimeSpan.Zero;

        foreach (var pair in colors)
            SetBrush(pair.Key, Color.Parse(pair.Value), duration);

        var bg = Color.Parse(colors["BgBrush"]);
        var light = (bg.R + bg.G + bg.B) > 420;
        IsEffectiveLight = light;

        SetBrush("PressedBrush", Color.Parse(light ? "#E1E4EB" : "#303642"), duration);
        SetBrush("BorderSubtleBrush", Color.Parse(light ? "#ECEEF2" : "#22262E"), duration);
        SetBrush("DividerBrush", Color.Parse(light ? "#DDE0E8" : "#2E333E"), duration);
        SetBrush("DisabledTextBrush", Color.Parse(light ? "#9CA3AF" : "#5B6170"), duration);
        SetBrush("NavSelectedBrush", Color.Parse(colors["SelectedBrush"]), duration);
        SetBrush("NavAccentBrush", Color.Parse(colors["AccentBrush"]), duration);
        SetBrush("AccentStrongBrush", Color.Parse(colors["AccentBrush"]), duration);
        SetBrush("AccentStrongSoftBrush", Color.Parse(colors["SelectedBrush"]), duration);
        SetBrush("DangerFillBrush", Color.Parse(light ? "#C93B4B" : "#D14755"), duration);
        SetBrush("DangerFillHoverBrush", Color.Parse(light ? "#B2303F" : "#BA3947"), duration);
        SetBrush("MicaShellTintBrush", Color.Parse(light ? "#ECF7F8FA" : "#E6101114"), duration);
        SetBrush("MicaSidebarBrush", Color.Parse(light ? "#E6F1F2F5" : "#DE14161B"), duration);
        SetBrush("PrimaryTextBrush", Colors.White, TimeSpan.Zero);

        if (Avalonia.Application.Current is { } app)
            app.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;

        var fillTop = Color.Parse(colors["AccentHoverBrush"]);
        var fillBottom = Color.Parse(colors["AccentFillBrush"]);
        var hoverTop = light ? Color.Parse("#7B6EE6") : Color.Parse("#A498FF");
        var hoverBottom = Color.Parse(colors["AccentHoverBrush"]);

        _resources["PrimaryFillBrush"] = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = [new(fillTop, 0), new(fillBottom, 1)]
        };
        _resources["PrimaryHoverBrush"] = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = [new(hoverTop, 0), new(hoverBottom, 1)]
        };

        var text = Color.Parse(colors["TextBrush"]);
        var accent = Color.Parse(colors["AccentBrush"]);
        _resources["GlassEdgeBrush"] = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            [
                new(Color.FromArgb(48, text.R, text.G, text.B), 0),
                new(Color.FromArgb(14, text.R, text.G, text.B), 0.6),
                new(Color.FromArgb(28, text.R, text.G, text.B), 1)
            ]
        };
        _resources["BackdropGlowBrush"] = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = [new(Color.FromArgb(20, accent.R, accent.G, accent.B), 0), new(Colors.Transparent, 0.65)]
        };
    }

    private void SetBrush(string key, Color target, TimeSpan duration)
    {
        if (!_brushes.TryGetValue(key, out var brush))
        {
            brush = new SolidColorBrush(target);
            _brushes[key] = brush;
            _resources[key] = brush;
            return;
        }
        brush.Transitions = duration > TimeSpan.Zero
            ? [new ColorTransition { Property = SolidColorBrush.ColorProperty, Duration = duration, Easing = Launcher.Desktop.MotionEasing.Linear }]
            : null;
        brush.Color = target;
    }

    public static BitmapSource Decode(byte[] bytes)
    {
        if (bytes.Length > 24 * 1024 * 1024) throw new ArgumentException("壁纸不能超过 24 MB。");
        using var stream = new MemoryStream(bytes);
        return Bitmap.DecodeToWidth(stream, 1600);
    }

    public async Task<BitmapSource?> LoadWallpaperAsync(AppearanceSettings settings)
    {
        if (settings.WallpaperFile is null) return null;
        var path = Path.Combine(ImageDirectory, settings.Validate().WallpaperFile!);
        if (!File.Exists(path)) return null;
        return await Task.Run(() =>
        {
            if (new FileInfo(path).Length > 24 * 1024 * 1024) throw new ArgumentException("壁纸不能超过 24 MB。");
            return Decode(File.ReadAllBytes(path));
        });
    }

    public async Task<(AppearanceSettings Settings, BitmapSource Image)> ImportWallpaperAsync(string source, AppearanceSettings settings)
    {
        var extension = Path.GetExtension(source).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".bmp")) throw new ArgumentException("请选择 PNG、JPG 或 BMP 图片。");
        var bytes = await Task.Run(() =>
        {
            if (new FileInfo(source).Length > 24 * 1024 * 1024) throw new ArgumentException("壁纸不能超过 24 MB。");
            return File.ReadAllBytes(source);
        });
        var image = await Task.Run(() => Decode(bytes));
        Directory.CreateDirectory(ImageDirectory);
        var name = Guid.NewGuid().ToString("N") + extension;
        var path = Path.Combine(ImageDirectory, name);
        var next = settings with { WallpaperFile = name };
        try { await File.WriteAllBytesAsync(path, bytes); AppearanceStore.Write(_directory, next); }
        catch { if (File.Exists(path)) File.Delete(path); throw; }
        DeleteWallpaper(settings.WallpaperFile);
        return (next, image);
    }

    public void DeleteWallpaper(string? name)
    {
        if (name is null) return;
        try { var valid = new AppearanceSettings(WallpaperFile: name).Validate(); File.Delete(Path.Combine(ImageDirectory, valid.WallpaperFile!)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_platformSettings is not null)
        {
            _platformSettings.ColorValuesChanged -= OnPlatformColorValuesChanged;
            _platformSettings = null;
        }
    }
}
