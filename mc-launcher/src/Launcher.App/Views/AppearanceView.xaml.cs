using System.Windows.Controls;
namespace Launcher.App.Views;
public partial class AppearanceView : UserControl
{
    public AppearanceView() { InitializeComponent(); IsVisibleChanged+=(_,e)=> { if(e.NewValue is true) Dispatcher.BeginInvoke(()=>ThemeList.Focus()); }; }
}
