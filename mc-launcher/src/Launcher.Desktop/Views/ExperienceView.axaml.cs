using Avalonia.Controls;
using Avalonia.Interactivity;
namespace Launcher.Desktop.Views;
public sealed partial class ExperienceView:UserControl
{
    public ExperienceView()=>InitializeComponent();
    private void ResetPreview(object? sender,RoutedEventArgs e)=>WardrobePlayerPreview.ResetView();
    private void BackPreview(object? sender,RoutedEventArgs e)=>WardrobePlayerPreview.ShowBack();
}
