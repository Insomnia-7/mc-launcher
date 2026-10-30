using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.Application.Diagnostics;
using Launcher.Application.Settings;

namespace Launcher.App.ViewModels;

public sealed partial class MainViewModel
{
    public IReadOnlyList<string> ProxyModes { get; } = ["自动选择（系统 / 环境变量）", "直接连接（不使用代理）", "自定义代理"];
    [ObservableProperty] private bool _isNetworkOpen;
    [ObservableProperty] private int _proxyModeIndex;
    [ObservableProperty] private string _proxyAddress = "";
    [ObservableProperty] private string _networkHint = "";

    /// <summary>诊断卡片是否显示「网络设置…」——只有认出网络类失败时才出现。</summary>
    [ObservableProperty] private bool _showsNetworkFix;

    public bool IsCustomProxy => ProxyModeIndex == (int)ProxyMode.Custom;
    public string ActiveNetworkText => Launcher.Infrastructure.Http.HttpClientFactory.DescribeProxy();

    partial void OnProxyModeIndexChanged(int value) => OnPropertyChanged(nameof(IsCustomProxy));

    /// <summary>
    /// 失败文案 + （认出网络原因时）当前网络模式，并把「网络设置…」按钮亮出来。
    /// 用户在「代理连不上」时最想知道的就是自己现在到底走的是直连还是代理。
    /// </summary>
    private string DescribeFailure(string prefix, Exception exception)
    {
        var text = prefix + ErrorText.Describe(exception);
        if (NetworkErrors.Classify(exception) == NetworkFailure.None)
        {
            ShowsNetworkFix = false;
            return text;
        }

        ShowsNetworkFix = true;
        return text.Contains("当前网络", StringComparison.Ordinal)
            ? text
            : text + "\n当前网络：" + ActiveNetworkText;
    }

    [RelayCommand]
    private void OpenNetwork()
    {
        BeforeNetworkSettings?.Invoke();
        NetworkHint = "保存后请重启启动器；本次运行和正在进行的下载继续使用当前设置。";
        try
        {
            var settings = ProxySettingsStore.Read(AppPaths.DataDirectory);
            ProxyModeIndex = (int)settings.Mode;
            ProxyAddress = settings.Address;
        }
        catch (Exception)
        {
            ProxyModeIndex = 0;
            ProxyAddress = "";
            NetworkHint = "读取代理配置失败，请重新选择并保存。若设置文件本身损坏，请先修复 settings.json。";
        }
        OnPropertyChanged(nameof(ActiveNetworkText));
        IsNetworkOpen = true;
    }

    [RelayCommand]
    private void CloseNetwork() => IsNetworkOpen = false;

    [RelayCommand]
    private void SaveNetwork()
    {
        try
        {
            var settings = new ProxySettings((ProxyMode)ProxyModeIndex, ProxyAddress).Validate();
            ProxySettingsStore.Write(settings, AppPaths.DataDirectory);
            ProxyAddress = settings.Address;
            NetworkHint = "已保存。请重启启动器以应用网络设置。";
        }
        catch (ArgumentException ex) { NetworkHint = ex.Message; }
        catch (Exception) { NetworkHint = "保存失败，原设置已保留。请检查设置文件格式、写入权限或磁盘空间后重试。"; }
    }
}
