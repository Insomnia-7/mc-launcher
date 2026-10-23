using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Launcher.App;
using Launcher.App.Animations;
using Launcher.App.Appearance;
using Launcher.App.ViewModels;
using Launcher.Application.Settings;

internal static partial class UiCheck
{
    private static void Appearance()
    {
        var vm=(MainViewModel)Resolve(typeof(MainViewModel))!;
        vm.CloseSetupCommand.Execute(null); vm.IsInstanceSection=false;
        vm.Download.SelectedCategory=vm.Download.Categories.Single(c=>!c.IsSeparator && c.Category==Launcher.Domain.Content.DownloadCategory.Mod);
        Pump(vm.Download.SearchCommand.ExecuteAsync(null));
        vm.StatusText="就绪 · 界面演示，使用隔离测试数据";
        Check("构造外观不解码壁纸",vm.WallpaperImage is null && vm.ThemeChoices.Count==4);
        var window=new MainWindow(vm);
        foreach(var choice in vm.ThemeChoices.ToArray()) {
            vm.SelectedTheme=choice;
            Check("主题即时替换全局画刷："+choice.Name,((SolidColorBrush)System.Windows.Application.Current.Resources["BgBrush"]).Color==(Color)ColorConverter.ConvertFromString(ThemeCatalog.Resolve(choice.Definition)["BgBrush"]));
            Render(window,1000,640,"appearance-"+choice.Id+".png");
            if(choice.Id=="glacier") {
                var primary=new Button { Content="搜索",Style=(Style)System.Windows.Application.Current.Resources["PrimaryButton"] };
                var buttonHost=new Grid { Children={primary} }; buttonHost.Measure(new Size(140,50));buttonHost.Arrange(new Rect(0,0,140,50));primary.ApplyTemplate();buttonHost.UpdateLayout();
                var label=AppearanceDescendants(primary).OfType<TextBlock>().First();
                Check("浅色主题的主按钮文字保持白色",((SolidColorBrush)label.Foreground).Color==Colors.White);
            }
        }
        vm.SelectedTheme=vm.ThemeChoices[0];
        vm.OpenAppearanceCommand.Execute(null);
        Check("外观面板可独立打开",vm.IsAppearanceOpen);
        Render(window,820,560,"appearance-panel-820.png");
        vm.CloseTopOverlayCommand.Execute(null);
        Check("Escape 关闭最上层外观面板",!vm.IsAppearanceOpen);
        var root=Path.Combine(AppPaths.DataDirectory,"appearance-fixtures"); Directory.CreateDirectory(root);
        var wallpaper=Path.Combine(root,"wallpaper.png");
        var visual=new DrawingVisual();
        using(var drawing=visual.RenderOpen()) {
            drawing.DrawRectangle(new LinearGradientBrush(Color.FromRgb(15,42,70),Color.FromRgb(124,86,137),32),null,new Rect(0,0,960,540));
            drawing.DrawEllipse(new SolidColorBrush(Color.FromArgb(130,81,192,210)),null,new Point(220,180),290,160);
            drawing.DrawEllipse(new SolidColorBrush(Color.FromArgb(120,233,162,139)),null,new Point(770,380),360,220);
        }
        var bitmap=new RenderTargetBitmap(960,540,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(wallpaper))encoder.Save(stream);
        Pump(vm.ImportAppearanceWallpaperAsync(wallpaper));
        Check("壁纸导入成功且为可跨线程使用的图像",vm.WallpaperImage is { IsFrozen:true } && File.Exists(wallpaper));
        var stored=AppearanceStore.Read(AppPaths.DataDirectory);var savedImage=Path.Combine(AppPaths.DataDirectory,"appearance-data",stored.WallpaperFile!);
        Check("壁纸复制到内部目录且原文件保留",File.Exists(savedImage) && savedImage!=wallpaper);
        vm.WallpaperOpacity=0.72;vm.SaveAppearanceCommand.Execute(null);
        Check("壁纸强度保存",AppearanceStore.Read(AppPaths.DataDirectory).WallpaperOpacity==0.72);
        Render(window,1000,640,"appearance-wallpaper.png");
        var current=vm.WallpaperImage;
        var filesBefore=Directory.GetFiles(Path.Combine(AppPaths.DataDirectory,"appearance-data")).Length;
        var saveFailed=false;
        using(var held=new FileStream(AppearanceStore.SettingsPath(AppPaths.DataDirectory),FileMode.Open,FileAccess.ReadWrite,FileShare.None))
        {
            try { Pump(vm.ImportAppearanceWallpaperAsync(wallpaper)); } catch { saveFailed=true; }
        }
        Check("壁纸保存失败不留新副本并保留原图",saveFailed && ReferenceEquals(vm.WallpaperImage,current) && File.Exists(savedImage) && Directory.GetFiles(Path.Combine(AppPaths.DataDirectory,"appearance-data")).Length==filesBefore);
        var invalid=Path.Combine(root,"invalid.png");File.WriteAllText(invalid,"not an image");
        var rejected=false;try{Pump(vm.ImportAppearanceWallpaperAsync(invalid));}catch{rejected=true;}
        Check("无效图片不会改变原壁纸或卡住按钮",rejected && ReferenceEquals(vm.WallpaperImage,current) && !vm.IsWallpaperBusy && File.Exists(savedImage));
        var customPath=Path.Combine(root,"theme.json");AppearanceStore.Export(customPath,new(1,"日落玻璃",new() { ["AccentBrush"]="#FFBB99",["BgBrush"]="#2A1B29" }));
        vm.ImportAppearanceTheme(customPath);
        Check("自定义主题导入并成为可选项",vm.SelectedTheme?.Id=="custom" && vm.ThemeChoices.Count==5);
        var restored=(MainViewModel)Resolve(typeof(MainViewModel))!;
        Check("主题与壁纸设置重新打开可恢复",restored.SelectedTheme?.Name=="日落玻璃" && restored.WallpaperOpacity==0.72 && restored.WallpaperImage is null);
        Pump(restored.LoadAppearanceWallpaperAsync());
        Check("首帧后可恢复本地壁纸",restored.WallpaperImage is not null);
        vm.RemoveWallpaperCommand.Execute(null);
        Check("恢复主题背景清理内部副本且保留用户原图",vm.WallpaperImage is null && !File.Exists(savedImage) && File.Exists(wallpaper) && AppearanceStore.Read(AppPaths.DataDirectory).WallpaperFile is null);

        // Editable ComboBox must retain text entry after replacing the control template.
        var combo=new ComboBox { Style=(Style)System.Windows.Application.Current.Resources["DarkComboBox"],IsEditable=true,ItemsSource=new[] {"1.21.4","1.20.1"} };
        combo.ApplyTemplate();var editor=(TextBox)combo.Template.FindName("PART_EditableTextBox",combo);
        editor.Text="custom-version";editor.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
        Check("统一下拉框仍支持输入游戏版本",combo.Text=="custom-version");
        var outgoing=new Border();var incoming=new Border { Visibility=Visibility.Collapsed };
        var host=new Window { Content=new Grid { Children={outgoing,incoming} },Width=180,Height=100,Left=-10000,Top=-10000,ShowActivated=false,ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.Manual };
        PageVisibility.SetIsActive(outgoing,true);host.Show();
        PageVisibility.SetIsActive(outgoing,false);PageVisibility.SetIsActive(incoming,true);
        PageVisibility.SetIsActive(outgoing,true);PageVisibility.SetIsActive(incoming,false);
        Pump(Task.Delay(300));
        Check("快速反复切页以最后状态为准且动画收尾",outgoing.Visibility==Visibility.Visible && outgoing.IsHitTestVisible && outgoing.Opacity==1 && incoming.Visibility==Visibility.Collapsed && !incoming.IsHitTestVisible);
        host.Close();
        vm.SelectedTheme=vm.ThemeChoices.First(t=>t.Id=="ocean");
        window.Close();
    }
    private static IEnumerable<DependencyObject> AppearanceDescendants(DependencyObject root)
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++) {
            var child=VisualTreeHelper.GetChild(root,i); yield return child;
            foreach(var descendant in AppearanceDescendants(child)) yield return descendant;
        }
    }}
