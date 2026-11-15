using System.ComponentModel;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Animation;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Launcher.App.ViewModels;
using Launcher.Desktop.Controls;
using Launcher.Desktop.Views;

namespace Launcher.Desktop;

public sealed partial class MainWindow : Window
{
    /// <summary>侧边栏四态（设计大纲 §3.2 B-1）。</summary>
    private enum SidebarState { Expanded, Compact, Minimal, Overlay }

    private readonly LibraryView _library = new();
    private DiscoverView? _discover;
    private DialogLayers? _dialogs;
    private ExperienceView? _experience;
    private PreferencesView? _preferences;
    private GameDetailView? _detail;
    private MultiplayerView? _multiplayer;
    private MainViewModel? _model;
    private bool _closingAfterPause;
    private SidebarState _sidebarState = SidebarState.Expanded;
    private bool _overlaySidebarOpen;
    private bool _captionSwapping;

    public MainWindow()
    {
        InitializeComponent();
        // Windows supports extended native hit-testing, including drag and snap layouts.
        // Other desktops keep their native frame until their platform builds are verified.
        if (OperatingSystem.IsWindows())
        {
            ExtendClientAreaToDecorationsHint=true;
            ExtendClientAreaTitleBarHeightHint=38;
            WindowDecorations=WindowDecorations.BorderOnly;
        }
        else
        {
            CaptionButtons.IsVisible=false;
            TitleDragArea.IsVisible=false;
        }
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty) UpdateCaption();
            if (e.Property == ActualTransparencyLevelProperty) UpdateShellMaterialState();
        };
        UpdateCaption();
        UpdateShellMaterialPreference();
        DataContextChanged += (_, _) =>
        {
            if (_model is not null) _model.PropertyChanged -= ModelChanged;
            _model = DataContext as MainViewModel;
            _library.DataContext = _model;
            if (_discover is not null) _discover.DataContext=_model;
            if (_dialogs is not null) _dialogs.DataContext=_model;
            if (_experience is not null) _experience.DataContext=_model;
            if (_preferences is not null) _preferences.DataContext=_model;
            if (_detail is not null) _detail.DataContext=_model;
            if (_multiplayer is not null) _multiplayer.DataContext=_model;
            if (_model is not null) _model.PropertyChanged += ModelChanged;
            UpdatePage();
            UpdateOverlay();
            UpdateShellMaterialPreference();
            Dispatcher.UIThread.Post(() => { UpdateSidebar(userInitiated: false); UpdateNavIndicator(animate: false); }, DispatcherPriority.Loaded);
        };
        MotionPreferences.Changed += UpdateMotion;
        UpdateMotion();
        // 侧栏宽度过渡期间、窗口缩放、DPI 变化时 NavList 的 Bounds 每帧都变：
        // 用 LayoutUpdated 持续落位（与 DiscoverView 的 SelectionHighlight 同一做法），
        // 避免只在属性变化当帧按旧 Bounds 算一次就卡住。
        NavListLayoutUpdated();
        SizeChanged += (_, _) => UpdateSidebar(userInitiated: false);
        Activated += (_, _) => Classes.Set("inactive", false);
        Deactivated += (_, _) => Classes.Set("inactive", true);
        Opened += (_,_) =>
        {
            Dispatcher.UIThread.Post(() => UpdateNavIndicator(animate: false), DispatcherPriority.Loaded);
        };
        Closing += async (_,e)=>
        {
            if(_model?.Multiplayer?.HasSession==true){e.Cancel=true;_model.ConfirmMultiplayerClose(Close);return;}
            if(!_closingAfterPause&&_model?.HasActivePersistentTask==true){e.Cancel=true;await _model.PauseTasksForShutdownAsync();_closingAfterPause=true;Close();}
        };
        Closed += (_, _) =>
        {
            if (_model is not null) _model.PropertyChanged -= ModelChanged;
            MotionPreferences.Changed -= UpdateMotion;
            _navIndicatorCts?.Cancel(); _navIndicatorCts?.Dispose(); _navIndicatorCts = null;
        };
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(this, true);
    }

    private void MinimizeWindow(object? sender,RoutedEventArgs e) => WindowState=WindowState.Minimized;
    private void ToggleMaximizeWindow(object? sender,RoutedEventArgs e) => WindowState=WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized;
    private void CloseWindow(object? sender,RoutedEventArgs e) => Close();

    /// <summary>最大化 / 还原：圆角与内边距瞬时切换，只有图标做 167ms 交叉淡入（大纲 A-2 A-6/A-7）。</summary>
    private async void UpdateCaption()
    {
        var maximized=WindowState==WindowState.Maximized;
        Classes.Set("maximized", maximized);
        var data=maximized?"M 4,2 L 12,2 12,10 M 2,4 L 10,4 10,12 2,12 Z":"M 2,2 L 12,2 12,12 2,12 Z";
        var label=maximized?"还原窗口":"最大化";
        ToolTip.SetTip(MaximizeButton,label);
        AutomationProperties.SetName(MaximizeButton,label);
        if (MaximizeGlyph.Data?.ToString()==data) return;
        if (!MotionPreferences.Enabled || !IsLoaded || _captionSwapping) { MaximizeGlyph.Data=Geometry.Parse(data); return; }
        _captionSwapping=true;
        try
        {
            await Motion.AnimateOpacity(MaximizeGlyph, 1, 0, MotionTiming.FasterMs, MotionEasing.Exit, CancellationToken.None);
            MaximizeGlyph.Data=Geometry.Parse(data);
            await Motion.AnimateOpacity(MaximizeGlyph, 0, 1, MotionTiming.FastMs, MotionEasing.Entrance, CancellationToken.None);
        }
        finally
        {
            // A second state change can arrive while the icon is fading.
            MaximizeGlyph.Data=Geometry.Parse(WindowState==WindowState.Maximized
                ? "M 4,2 L 12,2 12,10 M 2,4 L 10,4 10,12 2,12 Z" : "M 2,2 L 12,2 12,12 2,12 Z");
            MaximizeGlyph.Opacity=1; _captionSwapping=false;
        }
    }

    private void UpdateMotion() => Classes.Set("reduced-motion", !MotionPreferences.Enabled);

    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        // ShellPage 一次导航会发多条中间通知（先关旧页再开新页），
        // 指示条只在值稳定后落位，避免被中间态（如 "" 空页）闪隐藏。
        if (e.PropertyName == nameof(MainViewModel.HasCustomWallpaper)) UpdateShellMaterialPreference();
        if (e.PropertyName == nameof(MainViewModel.HasOverlay)) UpdateOverlay();
        if (e.PropertyName == nameof(MainViewModel.IsSidebarCollapsed) || e.PropertyName == nameof(MainViewModel.ShellPage))
        {
            UpdatePage();
            if (e.PropertyName == nameof(MainViewModel.ShellPage) && _overlaySidebarOpen && _sidebarState == SidebarState.Overlay)
            {
                _overlaySidebarOpen = false;
                UpdateSidebar(userInitiated: false);
            }
            if (e.PropertyName == nameof(MainViewModel.IsSidebarCollapsed)) UpdateSidebar(userInitiated: true);
            // 一次导航会发多条中间通知（连关 5 个 bool + 广播 ShellPage），
            // 用递增 revision 只让最后一次 Post 生效，避免多个 Animation 互相取消。
            var revision = ++_navIndicatorRevision;
            Dispatcher.UIThread.Post(() => { if (revision == _navIndicatorRevision) UpdateNavIndicator(); }, DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// 侧边栏状态机（Expanded / Compact / Minimal / Overlay）。
    /// 收放手感回到上一版：宽度是唯一被动画的量，内部布局在 t=0 到位，
    /// 由宽度过渡带着内容连续重排；窗口 resize 引起的变化一律瞬时。
    /// </summary>
    private void UpdateSidebar(bool userInitiated)
    {
        var panel = SidebarPanel;
        var width = Bounds.Width > 0 ? Bounds.Width : ClientSize.Width;
        var narrow = width > 0 && width < 900;
        SidebarState state;
        if (narrow) state = _overlaySidebarOpen ? SidebarState.Overlay : SidebarState.Minimal;
        else state = _model?.IsSidebarCollapsed == true || (width >= 900 && width < 1000) ? SidebarState.Compact : SidebarState.Expanded;

        var previous = _sidebarState;
        var userToggle = userInitiated && previous != state;
        _sidebarState = state;

        var target = state switch
        {
            SidebarState.Expanded or SidebarState.Overlay => 240d,
            SidebarState.Compact => 72d,
            _ => 0d
        };
        var expandedLayout = state is SidebarState.Expanded or SidebarState.Overlay;

        panel.Classes.Set("nav-compact", state == SidebarState.Compact);
        panel.Classes.Set("nav-minimal", state == SidebarState.Minimal);
        panel.Classes.Set("nav-overlay", state == SidebarState.Overlay);
        Grid.SetColumnSpan(panel, state == SidebarState.Overlay ? 2 : 1);
        panel.HorizontalAlignment = state == SidebarState.Overlay ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;

        // 品牌行、文字、导航标签与上一版一致：紧贴状态切换一次性到位，不参与补间。
        SidebarBrandExpanded.IsVisible = expandedLayout;
        SidebarBrandCollapsed.IsVisible = state == SidebarState.Compact;
        SidebarScrim.IsVisible = state == SidebarState.Overlay;
        SidebarHamburger.IsVisible = state == SidebarState.Minimal;
        PageContent.Classes.Set("minimal", state == SidebarState.Minimal);

        panel.Width = target;

        if (userToggle && state == SidebarState.Overlay) PlayOverlayEntrance();
    }

    /// <summary>浮层侧栏滑入：窄窗口下从左侧推 24px 入场。</summary>
    private async void PlayOverlayEntrance()
    {
        if (!MotionPreferences.Enabled) return;
        try { await Motion.AnimateTranslateX(SidebarPanel, -24, 0, MotionTiming.PanelMs, MotionEasing.Entrance, CancellationToken.None); }
        finally { SidebarPanel.RenderTransform = TransformOperations.Identity; }
    }

    private void OpenOverlaySidebar(object? sender, RoutedEventArgs e)
    {
        _overlaySidebarOpen = true;
        _sidebarState = SidebarState.Minimal;
        UpdateSidebar(userInitiated: true);
    }

    private void OnScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        _overlaySidebarOpen = false;
        UpdateSidebar(userInitiated: false);
    }

    /// <summary>导航按钮在 ShellPage 上的对应关系。experience 页按当前 tab 解析。</summary>
    private static readonly Dictionary<string, string> NavButtonForPage = new()
    {
        ["library"] = "LibraryNav",
        ["detail"] = "LibraryNav",
        ["discover"] = "DiscoverNav",
        ["multiplayer"] = "MultiplayerNav",
        ["settings"] = "SettingsEntry",
    };

    private string? NavButtonName()
    {
        if (_model is null) return null;
        if (_model.ShellPage == "experience")
            return _model.IsRolePage ? "WardrobeEntry" : _model.IsTaskPage ? "TaskCenterEntry" : null;
        return NavButtonForPage.TryGetValue(_model.ShellPage, out var name) ? name : null;
    }

    /// <summary>
    /// 共享蓝色指示条：整条导航只有一条，随 ShellPage 平移 250ms（大纲 B-4）。
    /// 位置与路由同步；首次显示直接落位不飞入；侧栏收起时按图标布局重算。
    /// </summary>
    private void UpdateNavIndicator(bool animate = true)
    {
        if (_model is null) return;
        var indicator = this.FindControl<Border>("NavIndicator");
        var list = this.FindControl<StackPanel>("NavList");
        if (indicator is null || list is null) return;
        if (NavButtonName() is not { } buttonName
            || this.FindControl<Button>(buttonName) is not { } target
            || !target.IsAttachedToVisualTree()
            || target.GetVisualAncestors().OfType<Control>().All(a => a != (Control)list))
        {
            indicator.IsVisible = false;
            return;
        }
        var origin = target.TranslatePoint(new Point(0, (target.Bounds.Height - indicator.Height) / 2), list);
        if (origin is not { } point) { indicator.IsVisible = false; return; }
        var transform = indicator.RenderTransform as TranslateTransform;
        if (transform is null) return;
        indicator.IsVisible = true;
        _navIndicatorPlaced = true;
        if (!animate || !MotionPreferences.Enabled)
        {
            transform.X = point.X - 10; transform.Y = point.Y;
            return;
        }
        _navIndicatorCts?.Cancel(); _navIndicatorCts?.Dispose();
        var cts = _navIndicatorCts = new CancellationTokenSource();
        var fromX = transform.X; var fromY = transform.Y;
        _ = AnimateIndicatorAsync(indicator, transform, fromX, fromY, point.X - 10, point.Y, cts.Token);
    }

    private CancellationTokenSource? _navIndicatorCts;
    private int _navIndicatorRevision;
    private bool _navIndicatorPlaced;

    /// <summary>布局每帧变化（侧栏宽度过渡、窗口缩放、折叠）时以 animate:false 跟随，不做飞入动画。</summary>
    private void NavListLayoutUpdated()
    {
        var list = this.FindControl<StackPanel>("NavList");
        if (list is null) return;
        list.LayoutUpdated += (_,_) =>
        {
            if (_model is null) return;
            if (!_navIndicatorPlaced || _navIndicatorCts is not null) UpdateNavIndicator(animate: false);
        };
    }

    private static async Task AnimateIndicatorAsync(Border indicator, TranslateTransform transform, double fromX, double fromY, double toX, double toY, CancellationToken token)
    {
        try
        {
            var animation = new Animation { Duration = MotionTiming.Normal, Easing = MotionEasing.Entrance, FillMode = FillMode.Forward };
            animation.Children.Add(new KeyFrame { Cue = new Cue(0), Setters = { new Setter(TranslateTransform.XProperty, fromX), new Setter(TranslateTransform.YProperty, fromY) } });
            animation.Children.Add(new KeyFrame { Cue = new Cue(1), Setters = { new Setter(TranslateTransform.XProperty, toX), new Setter(TranslateTransform.YProperty, toY) } });
            await animation.RunAsync(indicator, token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            // FillMode.Forward 只在动画正常完成时保留终值；取消/中断时显式写回终值，保证快速连续切换后位置一致。
            if (!token.IsCancellationRequested) { transform.X = toX; transform.Y = toY; }
        }
    }

    private void UpdatePage()
    {
        if (_model is null) return;
        PageHost.Content = _model.ShellPage switch
        {
            "discover" => _discover ??= new DiscoverView { DataContext = _model },
            "experience" => _experience ??= new ExperienceView { DataContext = _model },
            "settings" => _preferences ??= new PreferencesView { DataContext = _model },
            "detail" => _detail ??= new GameDetailView { DataContext = _model },
            "multiplayer" => _multiplayer ??= new MultiplayerView { DataContext = _model },
            _ => _library
        };
    }

    private void UpdateOverlay()
    {
        if (_model?.HasOverlay == true && _dialogs is null)
        {
            _dialogs=new DialogLayers { DataContext=_model };
            OverlayHost.Content=_dialogs;
        }
        OverlayHost.IsHitTestVisible=_model?.HasOverlay == true;
    }

    private void OnDragOver(object? sender, DragEventArgs e) => e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (_model is null || _model.IsSetupOpen) return;
        var paths = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray() ?? [];
        foreach (var path in paths)
        {
            if (path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) await _model.DropJarAsync(path);
            else { await _model.ImportModpackFileAsync(path); break; }
        }
    }
}
