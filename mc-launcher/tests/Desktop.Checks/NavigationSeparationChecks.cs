using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Launcher.App.ViewModels;
using Launcher.Desktop;
using Launcher.Desktop.Controls;
using Launcher.Desktop.Views;

internal static class NavigationSeparationChecks
{
    public static void Run(MainViewModel model,MainWindow window,Action<int> pump,Action<string,bool> check,Action<string,bool> capture)
    {
        T Find<T>(string name)where T:Control=>window.GetVisualDescendants().OfType<T>().Single(x=>x.Name==name);
        model.SelectedMotion=model.MotionChoices.Single(x=>x.Id=="off");
        foreach(var theme in new[]{"light","dark"})
        {
            model.SelectThemeModeCommand.Execute(theme);model.ShowHomeCommand.Execute(null);model.OpenNetworkCommand.Execute(null);pump(100);
            check($"{theme} 网络使用独立路由",model.ShellPage=="network"&&!model.IsSettingsPage&&!model.IsPreferencesOpen);
            var network=Find<AnimatedContentHost>("PageHost").Content;
            check($"{theme} 网络页不混入外观存储更新标签",network is NetworkView&&!window.GetVisualDescendants().OfType<Button>().Any(b=>b.Name is "AppearanceSettingsTab" or "StorageSettingsTab" or "LauncherUpdateSettingsTab"));
            check($"{theme} 网络为唯一主导航选中项",Find<Button>("NetworkNav").Classes.Contains("active")&&!Find<Button>("SettingsEntry").Classes.Contains("active"));
            var indicator=Find<Border>("NavIndicator");var transform=(TranslateTransform)indicator.RenderTransform!;var nav=Find<Button>("NetworkNav");var position=nav.TranslatePoint(default,Find<DockPanel>("NavList"))!.Value;
            check($"{theme} 网络导航背景与指示条对齐",indicator.IsVisible&&Find<Border>("NavSelection").IsVisible&&Math.Abs(transform.Y-(position.Y+(nav.Bounds.Height-indicator.Height)/2))<1);
            model.ProxyModeIndex=2;model.ProxyAddress="http://127.0.0.1:7892";pump(30);capture("network-separated-"+theme,true);
            check($"{theme} 全局下载源归属网络页",Find<ComboBox>("NetworkDownloadSource").IsEffectivelyVisible);
            model.OpenNetworkCommand.Execute(null);pump(30);
            check($"{theme} 还原代理设置留在网络页",model.ShellPage=="network"&&model.ProxyModeIndex!=2);
            model.OpenAppearanceCommand.Execute(null);pump(100);
            check($"{theme} 外观属于独立设置页面",model.IsSettingsPage&&!model.IsNetworkOpen&&Find<AnimatedContentHost>("PageHost").Content is PreferencesView);
            check($"{theme} 设置不再包含网络标签或代理输入",!window.GetVisualDescendants().OfType<Button>().Any(b=>b.Name=="NetworkSettingsTab")&&!window.GetVisualDescendants().OfType<NetworkView>().Any());
            check($"{theme} 设置为唯一主导航选中项",Find<Button>("SettingsEntry").Classes.Contains("active")&&!Find<Button>("NetworkNav").Classes.Contains("active"));
            capture("settings-separated-"+theme,true);
            model.OpenLauncherUpdatesCommand.Execute(null);pump(60);
            check($"{theme} 关于更新留在设置不跳网络",model.ShellPage=="settings"&&model.ShowsLauncherUpdates&&!model.IsNetworkOpen);
            model.OpenSettingsSyncCommand.Execute(null);pump(80);
            check($"{theme} 同步留在设置不跳网络",model.ShellPage=="settings"&&model.ShowsSettingsSync&&!model.IsNetworkOpen);
            model.OpenAppearanceCommand.Execute(null);pump(60);
            check($"{theme} 重返外观会清理旧设置子页",model.IsAppearanceSettings&&!model.ShowsSettingsSync&&!model.ShowsLauncherUpdates&&!model.ShowsStorage);
            model.OpenNetworkCommand.Execute(null);pump(80);
            check($"{theme} 返回网络使用独立已缓存页面",ReferenceEquals(network,Find<AnimatedContentHost>("PageHost").Content)&&model.ShellPage=="network");
        }
        for(var i=0;i<12;i++){if(i%2==0)model.OpenNetworkCommand.Execute(null);else model.OpenAppearanceCommand.Execute(null);pump(5);}
        pump(100);check("快速交替导航最终只显示设置页",model.ShellPage=="settings"&&Find<AnimatedContentHost>("PageHost").Content is PreferencesView&&!model.IsNetworkSettingsPage);
    }
}
