using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Launcher.App.ViewModels;

namespace Launcher.Desktop.Views;

public sealed partial class HomeView : UserControl
{
    private bool _entered;
    private CancellationTokenSource? _entrance;
    public HomeView()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (_entered) return;
            _entered = true;
            _entrance = new CancellationTokenSource();
            var pageMark = (TopLevel.GetTopLevel(this) as Window)?.FindControl<TextBlock>("TitleEdition");
            var stages = new List<Control>();
            if (pageMark is not null) stages.Add(pageMark);
            stages.AddRange([HomeHeroBanner, HomeQuickLaunchCard, HomeNetworkStatusCard, HomeInstancesCard, HomeShortcutsCard, HomeFooter]);
            await Motion.PlayStaggerSequenceAsync(stages, _entrance.Token);
        };
        Unloaded += (_, _) => { _entrance?.Cancel(); _entrance?.Dispose(); _entrance = null; };
    }
    private void OpenEntryMenu(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: LibraryEntry entry } button || DataContext is not MainViewModel model) return;
        var menu = new MenuFlyout();
        foreach (var (text, command) in new (string, System.Windows.Input.ICommand)[]
        {
            ("管理实例", model.OpenGameCommand), ("管理模组", model.OpenHomeEntryModsCommand),
            ("打开游戏目录", model.OpenHomeEntryDirectoryCommand), ("导出备份", model.ExportHomeEntryCommand),
            ("创建副本", model.CloneHomeEntryCommand), ("删除实例", model.DeleteHomeEntryCommand)
        }) menu.Items.Add(new MenuItem { Header = text, Command = command, CommandParameter = entry });
        FlyoutBase.SetAttachedFlyout(button, menu);
        menu.ShowAt(button);
    }
}
