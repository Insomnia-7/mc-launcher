using Avalonia.Controls;
namespace Launcher.Desktop.Views;
public sealed partial class LibraryView : UserControl
{
    private bool _entered;
    private CancellationTokenSource? _entrance;

    public LibraryView()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (_entered) return;
            _entered = true;
            _entrance = new CancellationTokenSource();
            await Motion.PlayStaggerSequenceAsync(new Control[] { LibraryHeading, QuickLaunchCard, LaunchProgressCard,
                LibraryActions, LibraryFilters, LibraryCards, LibraryList, GameRootStatus }.Where(c => c.IsVisible).ToArray(), _entrance.Token);
        };
        Unloaded += (_, _) => { _entrance?.Cancel(); _entrance?.Dispose(); _entrance = null; };
    }

    private void SelectGameFromList(object? sender,SelectionChangedEventArgs e)
    {
        if(sender is ListBox list&&list.SelectedItem is Launcher.App.ViewModels.LibraryEntry entry&&DataContext is Launcher.App.ViewModels.MainViewModel model)
        {model.OpenGameCommand.Execute(entry);list.SelectedItem=null;}
    }
}
