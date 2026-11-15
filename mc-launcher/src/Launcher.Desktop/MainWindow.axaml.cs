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

    private readonly HomeView _home = new();
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
            _home.DataContext = _model;
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
            _model?.Toasts.Dispose();
            PageHost.FinishTransition();
            _sidebarCts?.Cancel(); _sidebarCts?.Dispose(); _sidebarCts = null;
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

    private void UpdateMotion()
    {
        Classes.Set("reduced-motion", !MotionPreferences.Enabled);
        if (IsLoaded && _model is not null) { UpdateSidebar(userInitiated: false); UpdateNavIndicator(animate: false); }
    }

    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        // ShellPage 一次导航会发多条中间通知（先关旧页再开新页），
        // 指示条只在值稳定后落位，避免被中间态（如 "" 空页）闪隐藏。
        if (e.PropertyName == nameof(MainViewModel.HasCustomWallpaper)) UpdateShellMaterialPreference();
        if (e.PropertyName == nameof(MainViewModel.HasOverlay)) UpdateOverlay();
        if (e.PropertyName == nameof(MainViewModel.IsSidebarCollapsed) || e.PropertyName == nameof(MainViewModel.ShellPage))
        {
            var pageRevision = ++_pageRevision;
            Dispatcher.UIThread.Post(() => { if (pageRevision == _pageRevision) UpdatePage(); }, DispatcherPriority.Background);
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

    private int _pageRevision;

    private void UpdatePage()
    {
        if (_model is null) return;
        PageHost.Content = _model.ShellPage switch
        {
            "home" => _home,
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
            _dialogs.VisibilityChanged+=UpdateOverlay;
            OverlayHost.Content=_dialogs;
        }
        OverlayHost.IsHitTestVisible=_model?.HasOverlay == true || _dialogs?.HasVisibleDialogs == true;
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
