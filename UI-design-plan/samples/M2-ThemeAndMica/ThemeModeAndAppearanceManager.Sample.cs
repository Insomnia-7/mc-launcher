using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Launcher.Application.Settings;
using Launcher.Desktop;

namespace Launcher.App.Appearance;

public enum ThemeMode
{
    System,
    Light,
    Dark
}

public sealed class ThemeCoordinator : IDisposable
{
    private readonly string _dataDirectory;
    private readonly IResourceDictionary _resources;
    private readonly Dictionary<string, SolidColorBrush> _brushes = new(StringComparer.Ordinal);
    private IPlatformSettings? _platformSettings;
    private bool _disposed;

    public ThemeMode CurrentMode { get; private set; } = ThemeMode.Dark;
    public bool IsEffectiveDark { get; private set; } = true;
    public event Action? ThemeChanged;

    public ThemeCoordinator(string dataDirectory, IResourceDictionary resources)
    {
        _dataDirectory = dataDirectory;
        _resources = resources;
        _platformSettings = Avalonia.Application.Current?.PlatformSettings;
        if (_platformSettings is not null)
            _platformSettings.ColorValuesChanged += OnPlatformColorValuesChanged;
    }

    public static ThemeMode ParseMode(string? raw) => raw?.ToLowerInvariant() switch
    {
        "system" => ThemeMode.System,
        "light" or "glacier" => ThemeMode.Light,
        _ => ThemeMode.Dark
    };

    public static string SerializeMode(ThemeMode mode) => mode switch
    {
        ThemeMode.System => "system",
        ThemeMode.Light => "light",
        _ => "dark"
    };

    public void SetMode(ThemeMode mode, bool animate = true)
    {
        CurrentMode = mode;
        ApplyEffectiveTheme(animate);
    }

    public ThemeMode CycleMode()
    {
        var next = CurrentMode switch
        {
            ThemeMode.Dark => ThemeMode.Light,
            ThemeMode.Light => ThemeMode.System,
            _ => ThemeMode.Dark
        };
        SetMode(next, animate: true);
        return next;
    }

    private void OnPlatformColorValuesChanged(object? sender, PlatformColorValues values)
    {
        if (CurrentMode == ThemeMode.System)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyEffectiveTheme(animate: true));
    }

    private bool ResolveIsDark() => CurrentMode switch
    {
        ThemeMode.Dark => true,
        ThemeMode.Light => false,
        _ => _platformSettings?.GetColorValues().ThemeVariant != PlatformThemeVariant.Light
    };

    private void ApplyEffectiveTheme(bool animate)
    {
        var isDark = ResolveIsDark();
        IsEffectiveDark = isDark;
        var definition = DesktopThemes.ForVariant(isDark);
        var colors = ThemeCatalog.Resolve(definition);
        var duration = animate && MotionPreferences.Enabled
            ? TimeSpan.FromMilliseconds(200)
            : TimeSpan.Zero;

        foreach (var (key, hex) in colors)
        {
            var target = Color.Parse(hex);
            if (!_brushes.TryGetValue(key, out var brush))
            {
                brush = new SolidColorBrush(target);
                _brushes[key] = brush;
                _resources[key] = brush;
            }
            else
            {
                brush.Transitions = duration > TimeSpan.Zero
                    ? [new ColorTransition { Property = SolidColorBrush.ColorProperty, Duration = duration, Easing = MotionEasing.Linear }]
                    : null;
                brush.Color = target;
            }
        }

        UpdateDerivedBrushes(isDark, colors);

        if (Avalonia.Application.Current is { } app)
            app.RequestedThemeVariant = isDark ? ThemeVariant.Dark : ThemeVariant.Light;

        ThemeChanged?.Invoke();
    }

    private void UpdateDerivedBrushes(bool isDark, IReadOnlyDictionary<string, string> colors)
    {
        _resources["PrimaryTextBrush"] = new SolidColorBrush(Colors.White);
        _resources["PrimaryFillBrush"] = new SolidColorBrush(Color.Parse(colors["AccentFillBrush"]));
        _resources["PrimaryHoverBrush"] = new SolidColorBrush(Color.Parse(colors["AccentHoverBrush"]));
        _resources["NavSelectedBrush"] = new SolidColorBrush(Color.Parse(colors["SelectedBrush"]));
        _resources["AccentStrongBrush"] = new SolidColorBrush(Color.Parse(colors["AccentBrush"]));
        _resources["MicaShellTintBrush"] = new SolidColorBrush(Color.Parse(isDark ? "#E6101114" : "#ECF7F8FA"));
        _resources["MicaSidebarBrush"] = new SolidColorBrush(Color.Parse(isDark ? "#DE14161B" : "#E6F1F2F5"));
    }

    public static void ConfigureWindowMaterial(Window window, Border shellRoot, Border sidebarPanel, bool hasCustomWallpaper)
    {
        if (!OperatingSystem.IsWindows() || hasCustomWallpaper)
        {
            window.TransparencyLevelHint = [WindowTransparencyLevel.None];
            window.Background = (IBrush?)window.FindResource("BgBrush");
            shellRoot.Classes.Set("mica-active", false);
            sidebarPanel.Classes.Set("mica-active", false);
            return;
        }

        window.TransparencyLevelHint =
        [
            WindowTransparencyLevel.Mica,
            WindowTransparencyLevel.AcrylicBlur,
            WindowTransparencyLevel.None
        ];

        var micaActive = window.ActualTransparencyLevel is WindowTransparencyLevel.Mica or WindowTransparencyLevel.AcrylicBlur;
        window.Background = micaActive ? Brushes.Transparent : (IBrush?)window.FindResource("BgBrush");
        shellRoot.Classes.Set("mica-active", micaActive);
        sidebarPanel.Classes.Set("mica-active", micaActive);
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
