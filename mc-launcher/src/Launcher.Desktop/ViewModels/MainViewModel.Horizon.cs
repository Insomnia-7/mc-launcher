using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.Application.Auth;
using Launcher.Application.Diagnostics;
using Launcher.Application.Experience;
using Launcher.Domain.Install;
using Launcher.Domain.Versions;
using Launcher.Infrastructure.Horizon;
using Launcher.Infrastructure.Http;

namespace Launcher.App.ViewModels;

public sealed partial class MainViewModel
{
    private HorizonClient _horizon=null!;
    private CancellationTokenSource? _horizonCts;
    private readonly HashSet<string> _horizonRunning=[];
    [ObservableProperty] private bool _isHorizonOpen;
    [ObservableProperty] private bool _horizonBusy;
    [ObservableProperty] private string _horizonHint="安装官方构建或导入完整本地构建，然后新建 Rust 实例。";
    [ObservableProperty] private string _horizonName="我的 Rust 世界";
    [ObservableProperty] private HorizonBuild? _selectedHorizonBuild;
    [ObservableProperty] private HorizonInstance? _selectedHorizonInstance;
    public ObservableCollection<HorizonBuild> HorizonBuilds {get;}=[];
    public ObservableCollection<HorizonInstance> HorizonInstances {get;}=[];
    public bool IsHorizonPage=>ShellPage=="horizon";
    public bool HasHorizonInstances=>HorizonInstances.Count>0;
    public bool CanEditHorizon=>!HorizonBusy;
    public string HorizonSelectedInfo=>SelectedHorizonInstance is {} instance?$"{instance.Build.Title}\n独立存档与配置 · 不使用 Java 模组加载器":"选择一个 Rust 实例后启动。";
    public void InitializeHorizon(HorizonClient client)
    {
        _horizon=client;
        foreach(var build in ExperienceFiles.Read<List<HorizonBuild>>(Path.Combine(client.Root,"builds.json"))??[])HorizonBuilds.Add(build);
        foreach(var instance in ExperienceFiles.Read<List<HorizonInstance>>(Path.Combine(client.Root,"instances.json"))??[])HorizonInstances.Add(instance);
        SelectedHorizonBuild=HorizonBuilds.LastOrDefault();SelectedHorizonInstance=HorizonInstances.FirstOrDefault();
        HorizonInstances.CollectionChanged+=(_,_)=>{OnPropertyChanged(nameof(HasHorizonInstances));OnPropertyChanged(nameof(InstanceCountText));};
        OnPropertyChanged(nameof(HasHorizonInstances));OnPropertyChanged(nameof(InstanceCountText));
    }
    [RelayCommand] private void OpenHorizon(){BeforeMainNavigation?.Invoke();IsHorizonOpen=true;}
    partial void OnIsHorizonOpenChanged(bool value)=>NotifyWorkspaceNavigation();
    partial void OnSelectedHorizonInstanceChanged(HorizonInstance? value)=>OnPropertyChanged(nameof(HorizonSelectedInfo));
    partial void OnHorizonBusyChanged(bool value)=>OnPropertyChanged(nameof(CanEditHorizon));
    private async Task HorizonOperation(Func<CancellationToken,Task> action)
    {
        if(HorizonBusy)return;
        HorizonBusy=true;using var cts=new CancellationTokenSource();_horizonCts=cts;
        try{await action(cts.Token);}
        catch(OperationCanceledException){HorizonHint="已取消。已有实例与存档保留。";}
        catch(Exception ex){HorizonHint=ErrorText.Describe(ex);}
        finally{_horizonCts=null;HorizonBusy=false;}
    }
    [RelayCommand] private void CancelHorizon()=>_horizonCts?.Cancel();
    private void SaveHorizon()
    {
        ExperienceFiles.Write(Path.Combine(_horizon.Root,"builds.json"),HorizonBuilds.ToArray());
        ExperienceFiles.Write(Path.Combine(_horizon.Root,"instances.json"),HorizonInstances.ToArray());
    }
    private void AddHorizonBuild(HorizonBuild build)
    {
        var existing=HorizonBuilds.FirstOrDefault(b=>b.Directory==build.Directory);
        if(existing is not null)HorizonBuilds.Remove(existing);
        HorizonBuilds.Add(build);SelectedHorizonBuild=build;SaveHorizon();HorizonHint=$"构建已就绪：{build.Title}。可以新建独立 Rust 实例。";
    }
    [RelayCommand] private Task InstallHorizonAsync()=>HorizonOperation(async ct=>AddHorizonBuild(await _horizon.InstallNightlyAsync(new Progress<string>(text=>HorizonHint=text),ct)));
    [RelayCommand] private async Task ImportHorizonAsync()
    {
        if(HorizonBusy)return;
        var directory=await PlatformUi.PickFolderAsync("选择完整 Horizon 构建目录");if(directory is null)return;
        AskConfirm("导入本地 Rust 构建","导入时会运行构建的接口检测。请只选择你自行编译或可信来源的程序。",directory,"导入并检测",async()=>await ImportHorizonBuildAsync(directory));
    }
    public Task ImportHorizonBuildAsync(string directory)=>HorizonOperation(async ct=>AddHorizonBuild(await _horizon.InspectAsync(directory,ct)));
    [RelayCommand] private void CreateHorizonInstance()
    {
        if(HorizonBusy)return;
        if(SelectedHorizonBuild is not {} build){HorizonHint="请先安装或导入一个 Horizon 构建。";return;}
        var name=HorizonName.Trim();if(name.Length is 0 or >80){HorizonHint="请输入1–80个字符的实例名称。";return;}
        var instance=new HorizonInstance(Guid.NewGuid().ToString("N"),name,build);
        Directory.CreateDirectory(_horizon.GameDirectory(instance));HorizonInstances.Add(instance);SelectedHorizonInstance=instance;SaveHorizon();
        HorizonHint="Rust 实例已创建。首次启动前请准备游戏资源。";
    }
    private async Task<MicrosoftAccount> HorizonOwner(CancellationToken ct)
    {
        if(Account is null)throw new InvalidOperationException("Horizon 需要登录拥有 Minecraft: Java Edition 的微软账号。请使用右上角账号入口登录；Java 实例仍可使用原有离线身份。 ");
        var account=await EnsureFreshAccountAsync(ct)??throw new InvalidOperationException("请重新登录微软账号。");
        await _horizon.VerifyOwnerAsync(account,ct);return account;
    }
    [RelayCommand] private Task PrepareHorizonAsync()=>HorizonOperation(async ct=>
    {
        if(SelectedHorizonInstance is not {} instance){HorizonHint="请先创建并选择一个 Rust 实例。";return;}
        await HorizonOwner(ct);await _horizon.ValidateBuildAsync(instance.Build,ct);
        if((await _horizon.CheckAsync(instance.Build,ct)).ExitCode==0){HorizonHint="游戏资源已完整，可以启动。";return;}
        var build=instance.Build;var minecraft=Path.Combine(_horizon.Root,"minecraft",build.Minecraft);
        HorizonHint=$"正在为已验证的游戏账号准备 Minecraft {build.Minecraft} 文件…";
        await _installer.InstallAsync(build.Minecraft,minecraft,true,new Progress<Launcher.Application.Abstractions.DownloadProgress>(p=>HorizonHint=$"下载游戏资源 · {p.Percent:F0}%"),ct);
        var version=JsonSerializer.Deserialize<VersionJson>(await File.ReadAllTextAsync(Path.Combine(minecraft,"versions",build.Minecraft,build.Minecraft+".json"),ct))??throw new InvalidDataException("游戏版本信息读取失败。");
        var server=version.Downloads?.Server??throw new InvalidDataException("此版本缺少服务端资源地址。");
        HorizonHint="正在准备资源转换需要的服务端文件…";
        using(var http=HttpClientFactory.Create("Fangyu-Horizon/1.0",TimeSpan.FromMinutes(10)))
        {
            var downloader=new HttpDownloader(http);
            await downloader.DownloadAsync([new DownloadEntry(server.Url,"server.jar",server.Size,server.Sha1,DownloadKind.Client)],minecraft,ct:ct);
        }
        HorizonHint=$"正在准备转换工具使用的 Java {build.JavaMajor}…";
        var java=await _javaProvisioner.EnsureAsync(build.JavaMajor,AppPaths.DataDirectory,null,ct);
        var javaHome=Path.GetDirectoryName(Path.GetDirectoryName(java.ExecutablePath))!;
        await _horizon.PrepareAsync(build,minecraft,Path.Combine(minecraft,"server.jar"),javaHome,new Progress<string>(text=>HorizonHint=text),ct);
        HorizonHint="游戏资源准备完成，可以启动 Rust 客户端。";
    });
    [RelayCommand] private Task LaunchHorizonAsync()=>HorizonOperation(async ct=>
    {
        if(SelectedHorizonInstance is not {} instance){HorizonHint="请先选择 Rust 实例。";return;}
        if(_horizonRunning.Contains(instance.Id)){HorizonHint="这个 Rust 实例已经在运行。";return;}
        var account=await HorizonOwner(ct);
        HorizonHint="正在检查资源并启动 Rust 客户端…";
        var process=await _horizon.StartAsync(instance,account,ct);
        _horizonRunning.Add(instance.Id);HorizonHint=$"{instance.Name} 已启动。";
        _=WatchHorizon(process,instance);
    });
    private async Task WatchHorizon(Launcher.Application.Abstractions.IGameProcess process,HorizonInstance instance)
    {
        try{var code=await process.WaitForExitAsync();HorizonHint=code==0?$"{instance.Name} 已退出。":$"Rust 客户端退出（{code}）。请查看实例目录中的 fangyu-launch.log 和 crash-reports。";}
        catch(Exception ex){HorizonHint="读取 Rust 客户端退出结果失败："+ErrorText.Describe(ex);}
        finally{_horizonRunning.Remove(instance.Id);process.Dispose();}
    }
    [RelayCommand] private void OpenHorizonFolder()
    {
        if(SelectedHorizonInstance is {} instance)Process.Start(new ProcessStartInfo(_horizon.GameDirectory(instance)){UseShellExecute=true});
    }
}
