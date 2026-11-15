using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.App.Appearance;
using Launcher.Application.Settings;
using Launcher.Desktop;
using BitmapSource = Avalonia.Media.Imaging.Bitmap;

namespace Launcher.App.ViewModels;

public sealed record MotionChoice(string Id, string Name)
{
    public override string ToString() => Name;
}

public sealed record ThemeModeChoice(ThemeMode Mode, string Name)
{
    public override string ToString() => Name;
}

public sealed record ThemeChoice(string Id, ThemeDefinition Definition)
{
    public string Name => Definition.Name;
    public override string ToString() => Name;
    public IBrush PanelBrush => Pick("PanelBrush");
    public IBrush CardBrush => Pick("CardBrush");
    public IBrush LineBrush => Pick("LineBrush");
    private IBrush Pick(string key) => new SolidColorBrush(Color.Parse(ThemeCatalog.Resolve(Definition)[key]));
    public IBrush Swatch => new SolidColorBrush(Color.Parse(ThemeCatalog.Resolve(Definition)["BgBrush"]));
    public IBrush Accent => new SolidColorBrush(Color.Parse(ThemeCatalog.Resolve(Definition)["AccentBrush"]));
}

public sealed partial class MainViewModel
{
    private AppearanceManager _appearance = null!;
    private AppearanceSettings _appearanceSettings = new();
    private DispatcherTimer? _appearanceSaveTimer;
    private bool _readingAppearance;
    private bool _syncingThemeSelection;
    private int _wallpaperRevision;

    public IReadOnlyList<MotionChoice> MotionChoices { get; } =
    [
        new("smooth", "流畅动画"),
        new("system", "跟随系统"),
        new("off", "减少动画")
    ];

    public IReadOnlyList<ThemeModeChoice> ThemeModeChoices { get; } =
    [
        new(ThemeMode.Dark, "深色模式"),
        new(ThemeMode.Light, "浅色模式"),
        new(ThemeMode.System, "跟随系统")
    ];

    [ObservableProperty] private MotionChoice? _selectedMotion;
    public ObservableCollection<ThemeChoice> ThemeChoices { get; } = [];
    [ObservableProperty] private ThemeChoice? _selectedTheme;
    [ObservableProperty] private ThemeMode _currentThemeMode = ThemeMode.Dark;
    [ObservableProperty] private bool _isAppearanceOpen;
    [ObservableProperty] private string _appearanceHint = "支持深色、浅色与跟随系统三种模式，切换即时生效。";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCustomWallpaper))]
    private BitmapSource? _wallpaperImage;
    [ObservableProperty] private double _wallpaperOpacity = 0.55;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditAppearance))]
    private bool _isWallpaperBusy;

    public bool CanEditAppearance => !IsWallpaperBusy;
    public bool HasCustomWallpaper => WallpaperImage is not null;
    public bool IsDarkModeSelected => CurrentThemeMode == ThemeMode.Dark;
    public bool IsLightModeSelected => CurrentThemeMode == ThemeMode.Light;
    public bool IsSystemModeSelected => CurrentThemeMode == ThemeMode.System;

    public string ThemeModeLabel => CurrentThemeMode switch
    {
        ThemeMode.Light => "浅色",
        ThemeMode.System => "跟随系统",
        _ => "深色"
    };

    public string ThemeModeTooltip => $"当前主题：{ThemeModeLabel}（点击切换 深色 / 浅色 / 跟随系统）";

    // Sun / Moon / Monitor-Auto简洁线性几何路径
    public Geometry ThemeModeIconGeometry { get; private set; } =
        Geometry.Parse("M 12.5,3 A 5.5,5.5 0 1 0 13,13.5 A 6.5,6.5 0 1 1 12.5,3 Z");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSidebarExpanded))]
    [NotifyPropertyChangedFor(nameof(SidebarWidth))]
    private bool _isSidebarCollapsed;

    public bool IsSidebarExpanded
    {
        get => !IsSidebarCollapsed;
        set => IsSidebarCollapsed = !value;
    }

    public double SidebarWidth => IsSidebarCollapsed ? 72 : 240;

    private void InitializeAppearance()
    {
        _readingAppearance = true;
        _appearance = new(AppPaths.DataDirectory, Avalonia.Application.Current!.Resources);
        _appearance.SystemThemeChanged += OnSystemThemeChanged;
        InitializeDesktopShell();

        var settingsPath = AppearanceStore.SettingsPath(AppPaths.DataDirectory);
        var hasSavedFile = File.Exists(settingsPath);
        _appearanceSettings = _appearance.Read();

        foreach (var preset in DesktopThemes.Choices)
            ThemeChoices.Add(new(preset.Id, preset.Definition));

        var migrated = false;
        ThemeMode resolvedMode;
        if (!hasSavedFile)
        {
            resolvedMode = ThemeMode.Dark;
            _appearanceSettings = _appearanceSettings with { ThemeId = DesktopThemes.DarkId, ThemeMode = "dark", CustomTheme = null };
        }
        else if (_appearanceSettings.ThemeMode is "dark" or "light" or "system")
        {
            resolvedMode = _appearanceSettings.ThemeMode switch
            {
                "light" => ThemeMode.Light,
                "system" => ThemeMode.System,
                _ => ThemeMode.Dark
            };
            if (_appearanceSettings.CustomTheme is not null)
            {
                _appearanceSettings = _appearanceSettings with { CustomTheme = null };
                migrated = true;
            }
        }
        else
        {
            // Legacy config ("themeMode": "auto"): preserve "glacier" -> Light, "aurora" -> Dark, migrate legacy custom/other to Dark.
            if (_appearanceSettings.ThemeId == DesktopThemes.LightId && _appearanceSettings.CustomTheme is null)
            {
                resolvedMode = ThemeMode.Light;
                _appearanceSettings = _appearanceSettings with { ThemeMode = "light" };
            }
            else if (_appearanceSettings.ThemeId == DesktopThemes.DarkId && _appearanceSettings.CustomTheme is null)
            {
                resolvedMode = ThemeMode.Dark;
                _appearanceSettings = _appearanceSettings with { ThemeMode = "dark" };
            }
            else
            {
                resolvedMode = ThemeMode.Dark;
                _appearanceSettings = _appearanceSettings with { ThemeId = DesktopThemes.DarkId, ThemeMode = "dark", CustomTheme = null };
                migrated = true;
            }
        }

        WallpaperOpacity = _appearanceSettings.WallpaperOpacity;
        IsSidebarCollapsed = _appearanceSettings.SidebarCollapsed;
        SelectedMotion = MotionChoices.FirstOrDefault(m => m.Id == _appearanceSettings.MotionMode) ?? MotionChoices[0];
        MotionPreferences.Mode = _appearanceSettings.MotionMode;

        CurrentThemeMode = resolvedMode;
        ApplyCurrentThemeMode(animate: false);

        _readingAppearance = false;
        if (migrated) TrySaveAppearance();
    }

    private void OnSystemThemeChanged()
    {
        if (CurrentThemeMode == ThemeMode.System)
            ApplyCurrentThemeMode(animate: true);
    }

    private void ApplyCurrentThemeMode(bool animate)
    {
        var useLight = CurrentThemeMode switch
        {
            ThemeMode.Light => true,
            ThemeMode.Dark => false,
            _ => _appearance.IsSystemLight()
        };

        var targetId = useLight ? DesktopThemes.LightId : DesktopThemes.DarkId;
        var choice = ThemeChoices.FirstOrDefault(t => t.Id == targetId) ?? ThemeChoices[0];

        _syncingThemeSelection = true;
        SelectedTheme = choice;
        _syncingThemeSelection = false;

        _appearance.Apply(choice.Definition, animate);
        ThemeModeIconGeometry = CurrentThemeMode switch
        {
            ThemeMode.Light => Geometry.Parse("M 8,3.5 A 4.5,4.5 0 1 0 8,12.5 A 4.5,4.5 0 1 0 8,3.5 Z M 8,1 V 2.2 M 8,13.8 V 15 M 1,8 H 2.2 M 13.8,8 H 15 M 3.1,3.1 L 4,4 M 12,12 L 12.9,12.9 M 12.9,3.1 L 12,4 M 4,12 L 3.1,12.9"),
            ThemeMode.System => Geometry.Parse("M 2,3 H 14 V 11 H 2 Z M 5.5,13.5 H 10.5 M 8,11 V 13.5"),
            _ => Geometry.Parse("M 11.8,2.6 A 5.4,5.4 0 1 0 13.4,11.6 A 4.6,4.6 0 0 1 11.8,2.6 Z")
        };

        foreach (var prop in new[]
        {
            nameof(IsLightTheme),
            nameof(IsDarkModeSelected),
            nameof(IsLightModeSelected),
            nameof(IsSystemModeSelected),
            nameof(ThemeModeLabel),
            nameof(ThemeModeTooltip),
            nameof(ThemeModeIconGeometry)
        })
        {
            OnPropertyChanged(prop);
        }
    }

    public void SetThemeMode(ThemeMode mode)
    {
        if (CurrentThemeMode == mode && !_readingAppearance) return;
        CurrentThemeMode = mode;
        var modeKey = mode switch
        {
            ThemeMode.Light => "light",
            ThemeMode.System => "system",
            _ => "dark"
        };
        var useLight = mode switch
        {
            ThemeMode.Light => true,
            ThemeMode.Dark => false,
            _ => _appearance.IsSystemLight()
        };
        _appearanceSettings = _appearanceSettings with
        {
            ThemeMode = modeKey,
            ThemeId = useLight ? DesktopThemes.LightId : DesktopThemes.DarkId,
            CustomTheme = null
        };
        ApplyCurrentThemeMode(animate: !_readingAppearance);
        if (!_readingAppearance) SaveAppearance();
    }

    [RelayCommand]
    private void SelectThemeMode(string? mode)
    {
        var parsed = mode?.ToLowerInvariant() switch
        {
            "light" => ThemeMode.Light,
            "system" => ThemeMode.System,
            _ => ThemeMode.Dark
        };
        SetThemeMode(parsed);
    }

    [RelayCommand]
    private void CycleThemeMode()
    {
        var next = CurrentThemeMode switch
        {
            ThemeMode.Dark => ThemeMode.Light,
            ThemeMode.Light => ThemeMode.System,
            _ => ThemeMode.Dark
        };
        SetThemeMode(next);
    }

    public async Task LoadAppearanceWallpaperAsync()
    {
        var revision = ++_wallpaperRevision;
        try
        {
            var image = await _appearance.LoadWallpaperAsync(_appearanceSettings);
            if (revision == _wallpaperRevision)
            {
                var previous = WallpaperImage;
                WallpaperImage = image;
                previous?.Dispose();
            }
            else image?.Dispose();
        }
        catch (Exception) { AppearanceHint = "壁纸暂时无法读取，可以重新导入。"; }
    }

    partial void OnSelectedThemeChanged(ThemeChoice? value)
    {
        if (_readingAppearance || _syncingThemeSelection || value is null) return;
        try
        {
            var mode = value.Id == DesktopThemes.LightId ? ThemeMode.Light : ThemeMode.Dark;
            SetThemeMode(mode);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "切换主题失败：{Theme}", value.Id);
            AppearanceHint = "切换主题失败：" + ex.Message;
        }
    }

    partial void OnSelectedMotionChanged(MotionChoice? value)
    {
        if (_readingAppearance || value is null) return;
        MotionPreferences.Mode = value.Id;
        _appearanceSettings = _appearanceSettings with { MotionMode = value.Id };
        SaveAppearance();
    }

    partial void OnWallpaperOpacityChanged(double value)
    {
        if (_readingAppearance) return;
        _appearanceSettings = _appearanceSettings with { WallpaperOpacity = value };
        _appearanceSaveTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) => SaveAppearance());
        _appearanceSaveTimer.Stop();
        _appearanceSaveTimer.Start();
    }

    partial void OnIsSidebarCollapsedChanged(bool value)
    {
        if (_readingAppearance) return;
        _appearanceSettings = _appearanceSettings with { SidebarCollapsed = value };
        SaveAppearance();
    }

    [RelayCommand] private void OpenAppearance() { IsAppearanceOpen = true; }

    partial void OnIsAppearanceOpenChanged(bool value)
    {
        if (value) { IsNetworkOpen = false; IsExperienceOpen = false; IsAccountsOpen = false; IsSetupOpen = false; }
        OnPropertyChanged(nameof(IsPreferencesOpen));
    }

    [RelayCommand] private void CloseAppearance() { if (IsWallpaperBusy) return; if (TrySaveAppearance()) IsAppearanceOpen = false; }
    [RelayCommand] private void SaveAppearance() => TrySaveAppearance();

    public void FlushAppearance()
    {
        if (_appearanceSaveTimer?.IsEnabled is true) TrySaveAppearance();
        _appearance?.Dispose();
    }

    private bool TrySaveAppearance()
    {
        _appearanceSaveTimer?.Stop();
        try
        {
            AppearanceStore.Write(AppPaths.DataDirectory, _appearanceSettings);
            AppearanceHint = "外观已保存，下次打开会自动恢复。";
            return true;
        }
        catch (Exception)
        {
            AppearanceHint = "外观保存失败，请检查数据目录权限或磁盘空间；当前预览仍可使用。";
            return false;
        }
    }

    public async Task ImportAppearanceWallpaperAsync(string path)
    {
        if (IsWallpaperBusy) return;
        IsWallpaperBusy = true;
        AppearanceHint = "正在准备壁纸…";
        ++_wallpaperRevision;
        try
        {
            var imported = await _appearance.ImportWallpaperAsync(path, _appearanceSettings);
            _appearanceSettings = imported.Settings;
            var oldImage = WallpaperImage;
            WallpaperImage = imported.Image;
            oldImage?.Dispose();
            AppearanceHint = "壁纸已导入，调整下方强度可改变显示效果。";
        }
        finally { IsWallpaperBusy = false; }
    }

    [RelayCommand]
    private async Task ImportWallpaperAsync()
    {
        var path = await PlatformUi.PickFileAsync("导入壁纸", "图片 (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp");
        if (path is null) return;
        try { await ImportAppearanceWallpaperAsync(path); }
        catch (Exception) { AppearanceHint = "壁纸导入失败，请选择 24 MB 以内的有效图片；原壁纸已保留。"; }
    }

    [RelayCommand]
    private void RemoveWallpaper()
    {
        if (IsWallpaperBusy) return;
        var previous = _appearanceSettings.WallpaperFile;
        var next = _appearanceSettings with { WallpaperFile = null };
        try
        {
            AppearanceStore.Write(AppPaths.DataDirectory, next);
            _appearanceSettings = next;
            ++_wallpaperRevision;
            var oldImage = WallpaperImage;
            WallpaperImage = null;
            oldImage?.Dispose();
            _appearance.DeleteWallpaper(previous);
            AppearanceHint = "已恢复主题背景。";
        }
        catch (Exception) { AppearanceHint = "移除壁纸失败，原设置已保留。"; }
    }

    [RelayCommand]
    private void CloseTopOverlay()
    {
        if (IsConfirmOpen) ConfirmCancelCommand.Execute(null);
        else if (IsAccountsOpen) CloseAccountsCommand.Execute(null);
        else if (IsSetupOpen) CloseSetupCommand.Execute(null);
        else if (IsAppearanceOpen) CloseAppearanceCommand.Execute(null);
        else if (IsNetworkOpen) CloseNetworkCommand.Execute(null);
        else if (IsExperienceOpen) CloseExperienceCommand.Execute(null);
        else if (IsModPreviewOpen) CloseModPreview();
        else if (IsCloneEditorOpen) CloseCloneEditor();
        else if (IsDetailOpen) ShowInstances();
    }
}
