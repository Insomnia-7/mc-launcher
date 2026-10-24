using System.Windows.Media.Animation;
using System.IO;
using Launcher.App.Animations;
using System.Windows;
using System.Windows.Controls;
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
        Closed += (_,_) => { viewModel.Download.PropertyChanging -= categoryChanging; viewModel.PropertyChanging -= sectionChanging; viewModel.InstanceRevealed -= RevealInstance; categoryMotion.Clear(); };
        DataContext = viewModel;
        viewModel.InstanceRevealed += RevealInstance;
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
    /// 装完一个版本后把它滚进视野并给一次高亮脉冲。
    /// <para>
    /// 只设 SelectedItem 不够：列表是虚拟化的，滚到别处时用户看不到选中项，会以为没装成功。
    /// 容器可能还没生成（刚 Add 进集合），所以找不到就等一帧再试一次。
    /// </para>
    /// </summary>
    private void RevealInstance(InstanceCard card)
    {
        InstanceList.ScrollIntoView(card);
        TryPulse(card, retry: true);
    }

    private void TryPulse(InstanceCard card, bool retry)
    {
        if (InstanceList.ItemContainerGenerator.ContainerFromItem(card) is not ListBoxItem container)
        {
            if (!retry) return;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(() => TryPulse(card, retry: false)));
            return;
        }

        var pulse = new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(520))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        container.BeginAnimation(UIElement.OpacityProperty, pulse);
    }
}