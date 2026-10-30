using Avalonia.Controls;
namespace Launcher.Desktop.Views;
public sealed partial class LibraryView : UserControl
{
    public LibraryView()=>InitializeComponent();
    private void SelectGameFromList(object? sender,SelectionChangedEventArgs e)
    {
        if(sender is ListBox list&&list.SelectedItem is Launcher.App.ViewModels.LibraryEntry entry&&DataContext is Launcher.App.ViewModels.MainViewModel model)
        {model.OpenGameCommand.Execute(entry);list.SelectedItem=null;}
    }
}
