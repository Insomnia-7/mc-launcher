using Avalonia.Controls;
namespace Launcher.Desktop.Views;
public sealed partial class MultiplayerView:UserControl
{
    public MultiplayerView()
    {
        InitializeComponent();
        SizeChanged += (_, _) =>
        {
            var compact=Bounds.Width<900;
            MultiplayerBody.ColumnDefinitions=new ColumnDefinitions(compact?"*":"*,320");
            Grid.SetColumn(MultiplayerConnectPanel,compact?0:1);
            Grid.SetRow(MultiplayerConnectPanel,compact?1:0);
        };
    }
}
