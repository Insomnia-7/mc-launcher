using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
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
    public MainWindow()
    {
        InitializeComponent();
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
        Closed += (_, _) => { if (_model is not null) _model.PropertyChanged -= ModelChanged; MotionPreferences.Changed -= UpdateMotion; };
        InstanceList.DoubleTapped += (_, _) => { if (_model?.LaunchCommand.CanExecute(null) == true) _model.LaunchCommand.Execute(null); };
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(this, true);
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
