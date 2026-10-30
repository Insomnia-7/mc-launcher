using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using Launcher.App.ViewModels;
using Launcher.Desktop.Views;

namespace Launcher.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly LibraryView _library = new();
    private DiscoverView? _discover;
    private DialogLayers? _dialogs;
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
            if (_model is not null) _model.PropertyChanged += ModelChanged;
            UpdatePage();
            UpdateOverlay();
        };
        MotionPreferences.Changed += UpdateMotion;
        UpdateMotion();
        Closing += async (_,e)=>{if(!_closingAfterPause&&_model?.HasActivePersistentTask==true){e.Cancel=true;await _model.PauseTasksForShutdownAsync();_closingAfterPause=true;Close();}};
        Closed += (_, _) => { if (_model is not null) _model.PropertyChanged -= ModelChanged; MotionPreferences.Changed -= UpdateMotion; };
        InstanceList.DoubleTapped += (_, _) => { if (_model?.LaunchCommand.CanExecute(null) == true) _model.LaunchCommand.Execute(null); };
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
        if (e.PropertyName == nameof(MainViewModel.IsInstanceSection)) UpdatePage();
        if (e.PropertyName == nameof(MainViewModel.HasOverlay)) UpdateOverlay();
    }
    private void UpdatePage()
    {
        if (_model?.IsInstanceSection == false)
        {
            _discover ??= new DiscoverView { DataContext=_model };
            PageHost.Content=_discover;
        }
        else PageHost.Content=_library;
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
