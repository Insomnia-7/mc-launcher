using System.IO;
using System.Text.Json.Nodes;
using Launcher.App;
using Launcher.App.ViewModels;
using Launcher.Application.Settings;

internal static partial class UiCheck
{
    private static void Network()
    {
        var path = LauncherDataPaths.ResolveSettingsFile(AppPaths.DataDirectory);
        var old = File.Exists(path) ? File.ReadAllText(path) : null;
        try
        {
            File.WriteAllText(path, "{\"downloadSource\":\"bmclapi\",\"extra\":true}");
            var vm = (MainViewModel)Resolve(typeof(MainViewModel))!;
            vm.CloseSetupCommand.Execute(null);
            vm.OpenNetworkCommand.Execute(null);
            Check("网络设置默认自动且打开不发起网络请求", vm.IsNetworkOpen && vm.ProxyModeIndex == 0 && !vm.IsCustomProxy);
            var active = vm.ActiveNetworkText;
            vm.ProxyModeIndex = 2; vm.ProxyAddress = "invalid";
            var original = File.ReadAllText(path);
            vm.SaveNetworkCommand.Execute(null);
            Check("无效代理保持编辑界面且不覆盖配置", vm.IsNetworkOpen && vm.NetworkHint.Contains("请输入") && File.ReadAllText(path) == original);
            vm.ProxyAddress = "http://127.0.0.1:7890";
            vm.SaveNetworkCommand.Execute(null);
            Check("自定义代理保存并提示重启", vm.IsCustomProxy && vm.NetworkHint.Contains("重启") && ProxySettingsStore.Read(AppPaths.DataDirectory).Mode == ProxyMode.Custom);
            Check("保存代理不改变当前进程网络策略", vm.ActiveNetworkText == active);
            Check("代理界面保存保留其它配置", JsonNode.Parse(File.ReadAllText(path))!["extra"]!.GetValue<bool>());
            var window = new MainWindow(vm);
            Render(window, 1000, 640, "network-1000.png");
            Render(window, 820, 560, "network-820.png");

            // 状态条上的「网络设置…」只在认出网络类失败时出现
            var entry = (System.Windows.Controls.Button)window.FindName("NetworkFixEntry");
            Check("状态条网络入口默认隐藏",
                entry is not null && entry.Visibility == System.Windows.Visibility.Collapsed);
            vm.ShowsNetworkFix = true;
            window.UpdateLayout();
            Check("网络失败时状态条露出入口",
                entry!.Visibility == System.Windows.Visibility.Visible);
            vm.ShowsNetworkFix = false;
            window.UpdateLayout();
            Check("恢复正常后入口收回",
                entry.Visibility == System.Windows.Visibility.Collapsed);

            window.Close();
            vm.CloseNetworkCommand.Execute(null);
            vm.ProxyAddress = "discarded edit";
            vm.OpenNetworkCommand.Execute(null);
            Check("关闭不保存且重新打开恢复已保存代理", vm.ProxyAddress == "http://127.0.0.1:7890");
            vm.ProxyModeIndex = 1; vm.SaveNetworkCommand.Execute(null);
            Check("切换直连隐藏地址并写入模式", !vm.IsCustomProxy && ProxySettingsStore.Read(AppPaths.DataDirectory) == new ProxySettings(ProxyMode.Direct));
            File.WriteAllText(path, "{broken");
            vm.OpenNetworkCommand.Execute(null); vm.SaveNetworkCommand.Execute(null);
            Check("配置损坏显示失败并保留文件", vm.NetworkHint.Contains("保存失败") && File.ReadAllText(path) == "{broken");
            File.WriteAllText(path, "{}");
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                vm.SaveNetworkCommand.Execute(null);
                Check("文件被占用时不显示保存成功", vm.NetworkHint.Contains("保存失败"));
            }
            vm.SaveNetworkCommand.Execute(null);
            Check("解除占用后可以重新保存", vm.NetworkHint.Contains("已保存"));
            vm.CloseNetworkCommand.Execute(null);
            Check("网络设置可正常关闭", !vm.IsNetworkOpen);
        }
        finally
        {
            if (old is null) File.Delete(path); else File.WriteAllText(path, old);
        }
    }
}
