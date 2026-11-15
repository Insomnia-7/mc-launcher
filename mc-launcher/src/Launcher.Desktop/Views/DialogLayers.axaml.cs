using Avalonia.Controls;
using Launcher.Desktop.Controls;
namespace Launcher.Desktop.Views;
public sealed partial class DialogLayers : UserControl
{
    public event Action? VisibilityChanged;
    private AnimatedDialog[] Dialogs => [SetupDialog,AccountsDialog,ConfirmDialog,RestartDialog];
    public bool HasVisibleDialogs => Dialogs.Any(d=>d.IsVisible);
    public DialogLayers()
    {
        InitializeComponent();
        foreach(var dialog in Dialogs)dialog.PropertyChanged+=(_,e)=>
        {if(e.Property==IsVisibleProperty || e.Property==AnimatedDialog.IsOpenProperty)VisibilityChanged?.Invoke();};
    }
}
