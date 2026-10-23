using System.IO;
using Launcher.App.Animations;
using System.Windows;
using System.Windows.Input;
using Launcher.App.ViewModels;

namespace Launcher.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        var categoryMotion = new CategoryTransition(DownloadPage, CategorySnapshot);
        System.ComponentModel.PropertyChangingEventHandler categoryChanging = (_,e) => {
            if(e.PropertyName==nameof(DownloadViewModel.SelectedCategory) && viewModel.IsDownloadSection) categoryMotion.Play();
        };
        System.ComponentModel.PropertyChangingEventHandler sectionChanging = (_,e) => {
            if(e.PropertyName==nameof(MainViewModel.IsInstanceSection)) categoryMotion.Clear();
        };
        viewModel.Download.PropertyChanging += categoryChanging;
        viewModel.PropertyChanging += sectionChanging;
        Closed += (_,_) => { viewModel.Download.PropertyChanging -= categoryChanging; viewModel.PropertyChanging -= sectionChanging; viewModel.AppearanceCrossfade -= PlayThemeFade; categoryMotion.Clear(); };
        DataContext = viewModel;
        viewModel.AppearanceCrossfade += PlayThemeFade;
        ContentRendered += async (_,_) => await viewModel.LoadAppearanceWallpaperAsync();
        Closing += (_,_) => viewModel.FlushAppearance();
    }

    /// <summary>点确认气泡外面的遮罩等于取消，和系统对话框的行为一致。</summary>
    private void OnConfirmBackdropClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.ConfirmCancelCommand.Execute(null);
    }

    /// <summary>双击版本 = 启动它（和 PCL / HMCL 的手感一致）。</summary>
    private void OnInstanceListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        if (viewModel.LaunchCommand.CanExecute(null)) viewModel.LaunchCommand.Execute(null);
    }

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    /// 拖进窗口的路由：<c>.jar</c> → 装进当前版本的 mods；目录 → 当 MultiMC / Prism 版本导入；
    /// 其它压缩包 → 当整合包导入。一次拖多个 .jar 会逐个放进去。
    /// </summary>
    private async void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        if (viewModel.IsSetupOpen) { e.Handled = true; return; }
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths) return;

        e.Handled = true;

        var jars = paths
            .Where(path => !Directory.Exists(path)
                           && Path.GetExtension(path).Equals(".jar", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (jars.Length > 0)
        {
            foreach (var jar in jars) await viewModel.DropJarAsync(jar);
            return;
        }

        await viewModel.ImportModpackFileAsync(paths[0]);
    }
    /// <summary>
    /// 切主题时先铺一层「旧底色」再淡出：颜色本身在 AppearanceManager 里插值，
    /// 这层遮罩负责兜住整屏的瞬时变化，看起来才像渐变而不是闪一下。
    /// </summary>
    private void PlayThemeFade(System.Windows.Media.Color previousBackground)
    {        if (Animations.MotionPreferences.Mode == "off") return;

        ThemeFade.Background = new System.Windows.Media.SolidColorBrush(previousBackground);
        ThemeFade.BeginAnimation(OpacityProperty, null);
        ThemeFade.Opacity = 0.88;

        var animation = new System.Windows.Media.Animation.DoubleAnimation(0.88, 0, TimeSpan.FromMilliseconds(360))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
            FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop,
        };
        ThemeFade.BeginAnimation(OpacityProperty, animation);
    }
}