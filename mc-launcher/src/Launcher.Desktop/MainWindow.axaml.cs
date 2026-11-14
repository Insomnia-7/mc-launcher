using System.ComponentModel;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Launcher.App.ViewModels;
using Launcher.Desktop.Views;

namespace Launcher.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly LibraryView _library = new();
    private DiscoverView? _discover;
    private DialogLayers? _dialogs;
    private ExperienceView? _experience;
    private PreferencesView? _preferences;
    private GameDetailView? _detail;
    private MultiplayerView? _multiplayer;
    private MainViewModel? _model;
    private bool _closingAfterPause;
    public MainWindow()
    {
        InitializeComponent();
        // Windows supports extended native hit-testing, including drag and snap layouts.
        // Other desktops keep their native frame until their platform builds are verified.
        if (OperatingSystem.IsWindows())
        {
            ExtendClientAreaToDecorationsHint=true;
            ExtendClientAreaTitleBarHeightHint=44;
            WindowDecorations=WindowDecorations.BorderOnly;
        }
        else
        {
            CaptionButtons.IsVisible=false;
            TitleDragArea.IsVisible=false;
        }
        PropertyChanged += (_, e) => { if(e.Property==WindowStateProperty) UpdateCaption(); };
        UpdateCaption();
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
            Dispatcher.UIThread.Post(() => UpdateNavIndicator(animate: false), DispatcherPriority.Loaded);
        };
        MotionPreferences.Changed += UpdateMotion;
        UpdateMotion();
        // 侧栏宽度 240ms 过渡期间、窗口缩放、DPI 变化时 NavList 的 Bounds 每帧都变：
        // 用 LayoutUpdated 持续落位（与 DiscoverView 的 SelectionHighlight 同一做法），
        // 避免只在属性变化当帧按旧 Bounds 算一次就卡住（F4/F10）。
        NavListLayoutUpdated();
        Opened += (_,_) => Dispatcher.UIThread.Post(() => UpdateNavIndicator(animate: false), DispatcherPriority.Loaded);
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
    private void UpdateCaption()
    {
        var maximized=WindowState==WindowState.Maximized;
        MaximizeGlyph.Data=Geometry.Parse(maximized?"M 4,2 L 12,2 12,10 M 2,4 L 10,4 10,12 2,12 Z":"M 2,2 L 12,2 12,12 2,12 Z");
        var label=maximized?"还原窗口":"最大化";
        ToolTip.SetTip(MaximizeButton,label);
        AutomationProperties.SetName(MaximizeButton,label);
    }
    private void UpdateMotion() => Classes.Set("reduced-motion", !MotionPreferences.Enabled);
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        // ShellPage 一次导航会发多条中间通知（先关旧页再开新页），
        // 指示条只在值稳定后落位，避免被中间态（如 "" 空页）闪隐藏。
        if (e.PropertyName == nameof(MainViewModel.HasOverlay)) UpdateOverlay();
        if (e.PropertyName == nameof(MainViewModel.IsSidebarCollapsed) || e.PropertyName == nameof(MainViewModel.ShellPage))
        {
            UpdatePage();
            // 一次导航会发多条中间通知（连关 5 个 bool + 广播 ShellPage），
            // 用递增 revision 只让最后一次 Post 生效，避免多个 Animation 互相取消（F5）。
            var revision = ++_navIndicatorRevision;
            Dispatcher.UIThread.Post(() => { if (revision == _navIndicatorRevision) UpdateNavIndicator(); }, DispatcherPriority.Background);
        }
    }
    /// <summary>导航按钮在 ShellPage 上的对应关系。experience 页按当前 tab 解析，不再隐藏蓝条（F7）。</summary>
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
    /// 共享蓝色指示条：整条导航只有一条，随 ShellPage 平滑移动（大纲 7）。
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
            // 首次未落位时也要跟随（冷启动 attach 前 TranslatePoint 可能为 null，靠这里兜底 F8）；
            // 已落位后持续跟随布局过渡，避免按旧 Bounds 卡住（F4）。
            if (_model is null) return;
            if (!_navIndicatorPlaced || _navIndicatorCts is not null) UpdateNavIndicator(animate: false);
        };
    }
    private static async Task AnimateIndicatorAsync(Border indicator, TranslateTransform transform, double fromX, double fromY, double toX, double toY, CancellationToken token)
    {
        try
        {
            var animation = new Animation { Duration = MotionTiming.Page, Easing = new CubicEaseOut(), FillMode = FillMode.Forward };
            animation.Children.Add(new KeyFrame { Cue = new Cue(0), Setters = { new Setter(TranslateTransform.XProperty, fromX), new Setter(TranslateTransform.YProperty, fromY) } });
            animation.Children.Add(new KeyFrame { Cue = new Cue(1), Setters = { new Setter(TranslateTransform.XProperty, toX), new Setter(TranslateTransform.YProperty, toY) } });
            await animation.RunAsync(indicator, token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            // FillMode.Forward 只在动画正常完成时保留终值；取消/中断时若被释放，transform 可能回落
            // 到旧值或停在中间帧。显式写回 base value，保证快速连续切换后位置一致（F5）。
            if (!token.IsCancellationRequested) { transform.X = toX; transform.Y = toY; }
        }
    }
    private void UpdatePage()
    {
        PageHost.Content=_model?.ShellPage switch
        {
            "discover"=>_discover??=new DiscoverView{DataContext=_model},
            "experience"=>_experience??=new ExperienceView{DataContext=_model},
            "settings"=>_preferences??=new PreferencesView{DataContext=_model},
            "detail"=>_detail??=new GameDetailView{DataContext=_model},
            "multiplayer"=>_multiplayer??=new MultiplayerView{DataContext=_model},
            _=>_library
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
