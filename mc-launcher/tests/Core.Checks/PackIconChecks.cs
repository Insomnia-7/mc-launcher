using System.IO.Compression;
using System.Text.Json;
using Launcher.Application.Abstractions;
using Launcher.Application.Experience;
using Launcher.Application.Install;
using Launcher.Application.Mods;
using Launcher.Infrastructure.Modpacks;

internal static class PackIconChecks
{
    public static async Task RunAsync(Action<string,bool,string?> check)
    {
        var root=Path.Combine(Path.GetTempPath(),"fangyu-icons-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var png=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j5f8AAAAASUVORK5CYII=");
        void Check(string name,bool result)=>check(name,result,null);
        string Pack(string name,Dictionary<string,byte[]> content,bool curse=false,string prefix="")
        {
            var path=Path.Combine(root,name+".zip");using var zip=ZipFile.Open(path,ZipArchiveMode.Create);
            using(var writer=new StreamWriter(zip.CreateEntry(prefix+(curse?"manifest.json":"modrinth.index.json")).Open()))
                writer.Write(curse?JsonSerializer.Serialize(new {name,version="1",manifestType="minecraftModpack",manifestVersion=1,minecraft=new{version="1.20.1",modLoaders=Array.Empty<object>()},files=Array.Empty<object>(),overrides="custom"}):
                    JsonSerializer.Serialize(new{formatVersion=1,game="minecraft",name,versionId="1",dependencies=new Dictionary<string,string>{{"minecraft","1.20.1"}},files=Array.Empty<object>()}));
            foreach(var (relative,bytes) in content)using(var output=zip.CreateEntry(prefix+relative).Open())output.Write(bytes);
            return path;
        }
        var sink=new FakeDownloader((_,_)=>null);var meta=new StubVersionMeta();var gameInstaller=new GameInstaller(sink,meta);
        var installer=new ModpackInstaller(gameInstaller,new(new StubLoaderMeta(),meta,gameInstaller),new ThrowingForgeInstaller(),new ThrowingNeoForgeInstaller(),sink);
        var factory=new ZipModpackSourceFactory(new StubCurseForgeClient());
        var gameRoot=Path.Combine(root,"game");var version=Path.Combine(gameRoot,"versions","1.20.1");Directory.CreateDirectory(version);File.WriteAllText(Path.Combine(version,"1.20.1.json"),"{}");
        async Task<ModpackImportReport> Import(string path,Func<CancellationToken,Task<byte[]?>>? fallback=null,CancellationToken ct=default)
        {await using var source=factory.Open(path);return await installer.ImportAsync(source,gameRoot,ct:ct,fallbackIcon:fallback);}
        string Icon(ModpackImportReport report)=>Path.Combine(gameRoot,"instances",report.InstanceId,ModpackIcon.FileName);
        try
        {
            var bundled=Pack("bundled",new(){{"icon.png",png}});var calls=0;
            var report=await Import(bundled,_=>{calls++;return Task.FromResult<byte[]?>(null);});
            Check("本地图标保存到实例且优先于平台图标",File.ReadAllBytes(Icon(report)).SequenceEqual(png)&&calls==0);
            File.Delete(bundled);Check("删除源包不影响持久化图标",File.Exists(Icon(report)));
            var nested=Pack("nested",new(){{"overrides/pack.png",png}},prefix:"folder/");
            await using(var source=factory.Open(nested))Check("套层目录的 overrides 图标可读取",(await source.ReadIconAsync())?.SequenceEqual(png)==true);
            var cf=Pack("curse",new(){{"custom/icon.png",png}},curse:true);
            await using(var source=factory.Open(cf))Check("CurseForge 自定义 overrides 图标可读取",(await source.ReadIconAsync())?.SequenceEqual(png)==true);
            var empty=Pack("empty",new(){{"overrides/resourcepacks/other/pack.png",png}});
            await using(var source=factory.Open(empty))Check("不把资源包子目录图标当成整合包图标",await source.ReadIconAsync()==null);
            var downloaded=await Import(empty,_=>Task.FromResult<byte[]?>(png));Check("包内无图标时保存平台图标",File.ReadAllBytes(Icon(downloaded)).SequenceEqual(png));
            var invalid=Pack("invalid",new(){{"icon.png","invalid"u8.ToArray()},{"overrides/icon.png",png}});
            await using(var source=factory.Open(invalid))Check("无效根图标继续查找包内有效图标",(await source.ReadIconAsync())?.SequenceEqual(png)==true);
            var large=Pack("large",new(){{"icon.png",new byte[ModpackIcon.MaxBytes+1]}});
            var largeReport=await Import(large);Check("过大或缺失图标不会阻止安装",!File.Exists(Icon(largeReport)));
            var offline=Pack("offline",new());var offlineReport=await Import(offline,_=>throw new HttpRequestException("offline"));
            Check("平台图标请求失败仍能安装",Directory.Exists(offlineReport.InstanceDirectory)&&!File.Exists(Icon(offlineReport)));
            var mmc=Path.Combine(root,"prism");Directory.CreateDirectory(mmc);File.WriteAllText(Path.Combine(mmc,"mmc-pack.json"),"""{"components":[{"uid":"net.minecraft","version":"1.20.1"}]}""");
            File.WriteAllText(Path.Combine(mmc,"instance.cfg"),"[General]\nname=Prism pack\niconKey=custom");File.WriteAllBytes(Path.Combine(mmc,"custom.png"),png);
            var mmcReport=await Import(mmc);Check("Prism 的 iconKey 图标随导入保存",File.ReadAllBytes(Icon(mmcReport)).SequenceEqual(png));
            var cancel=Pack("cancel",new());using var cts=new CancellationTokenSource();var canceled=false;
            try{await Import(cancel,token=>{cts.Cancel();token.ThrowIfCancellationRequested();return Task.FromResult<byte[]?>(png);},cts.Token);}catch(OperationCanceledException){canceled=true;}
            Check("读取平台图标时取消会回滚新实例",canceled&&!Directory.EnumerateFiles(Path.Combine(gameRoot,"instances"),"instance.json",SearchOption.AllDirectories).Any(p=>File.ReadAllText(p).Contains("\"cancel\"")));
            Check("取消后私有导入目录已清理",!Directory.EnumerateDirectories(Path.Combine(gameRoot,".cache","imports")).Any());
            Check("取消不会改动先前实例图标",File.ReadAllBytes(Icon(report)).SequenceEqual(png));

            var onlinePack=Pack("online",new());var archiveBytes=File.ReadAllBytes(onlinePack);
            var onlineSink=new FakeDownloader((_,_)=>archiveBytes);
            var mr=new StubModrinthClient{Projects=new(){{"online",new("online","online","Online pack",""){IconUrl="https://cdn.example/mr-logo.png"}}},
                Versions=new(){{"online",new[]{new ModrinthVersion("r1","online","Release","1",["1.20.1"],[],null,[new("https://cdn.example/online.mrpack","online.mrpack",archiveBytes.Length,true,null)],null)}}}};
            var presentation=new IconPresentation(png);
            var service=new ModpackDownloadService(onlineSink,mr,new StubCurseForgeClient(),factory,installer,presentation);
            var onlineReport=await service.InstallAsync(gameRoot,"online");
            Check("Modrinth 下载整合包保存项目图标",File.ReadAllBytes(Icon(onlineReport)).SequenceEqual(png)&&presentation.LastUrl=="https://cdn.example/mr-logo.png");
            var cfClient=new StubCurseForgeClient{Projects=new(){{42,new(){Id=42,Logo=new(){Url="https://cdn.example/cf-logo.png"}}}},Files=new(){{42,new[]{new CfFile{Id=7,ModId=42,FileName="cf-online.zip",FileLength=archiveBytes.Length,DownloadUrl="https://cdn.example/cf-online.zip"}}}}};
            service=new ModpackDownloadService(onlineSink,mr,cfClient,factory,installer,presentation);
            var cfReport=await service.InstallCurseForgeAsync(gameRoot,42);
            Check("CurseForge 下载整合包保存项目图标",File.ReadAllBytes(Icon(cfReport)).SequenceEqual(png)&&presentation.LastUrl=="https://cdn.example/cf-logo.png");
        }
        finally{ExperienceFiles.DeleteTree(root,Path.GetTempPath());}
    }

    private sealed class IconPresentation(byte[] bytes):IResourcePresentationClient
    {
        public string? LastUrl {get;private set;}
        public Task<byte[]?> GetIconAsync(string url,CancellationToken ct=default){LastUrl=url;return Task.FromResult<byte[]?>(bytes);}
        public Task<IReadOnlyList<ResourceTranslation>> GetTranslationsAsync(string source,IReadOnlyList<string> ids,CancellationToken ct=default)=>Task.FromResult<IReadOnlyList<ResourceTranslation>>([]);
    }
}
