using Avalonia.Controls;
using Avalonia.Interactivity;
namespace Launcher.Desktop.Views;
public sealed partial class LibraryView : UserControl
{
    private bool _showInspector = true;
    private Launcher.App.ViewModels.MainViewModel? _model;
    public LibraryView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateInspector();
        Loaded += (_, _) => UpdateInspector();
        DataContextChanged += (_, _) =>
        {
            if (_model is not null) _model.PropertyChanged -= ModelChanged;
            _model = DataContext as Launcher.App.ViewModels.MainViewModel;
            if (_model is not null) _model.PropertyChanged += ModelChanged;
            UpdateInspector();
        };
    }
    private void ModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Launcher.App.ViewModels.MainViewModel.HasInstances)) UpdateInspector();
    }
    private void ToggleInspector(object? sender, RoutedEventArgs e) { _showInspector = !_showInspector; UpdateInspector(); }
    private void UpdateInspector()
    {
        LibraryInspector.IsVisible = _showInspector && Bounds.Width >= 880 && DataContext is Launcher.App.ViewModels.MainViewModel { HasInstances: true };
    }
}
