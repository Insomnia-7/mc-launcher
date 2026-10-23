using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Launcher.Application.Abstractions;
using Launcher.Application.Install;
using Launcher.Application.Mods;
using Launcher.Application.Operations;
using Launcher.Application.Launch;
using Launcher.Domain.Content;
using Launcher.Domain.Install;
using Launcher.Domain.Modpacks;
using Launcher.Domain.Versions;
using Launcher.Domain.Launch;
using Launcher.Infrastructure.Meta;
using Launcher.Infrastructure.Modrinth;

internal static class FollowupChecks
{
    public static async Task RunAsync(Action<string, bool, string?> check)
    {
        Console.WriteLine("== 31. 模组更新事务、来源与共享任务 ==");
        var root = Path.Combine(Path.GetTempPath(), "mcl-followup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Coordinator(root, check);
            await UpdatesAsync(root, check);
            await SourcesAsync(root, check);
            await CacheAsync(root, check);
            await LaunchAsync(root, check);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Coordinator(string root, Action<string, bool, string?> check)
    {
        var coordinator = new GameOperationCoordinator();
        var first = coordinator.TryEnter(root, "导入")!;
        check("同一根目录的不同写任务互斥", coordinator.TryEnter(Path.Combine(root, "."), "下载") is null, null);
        check("冲突提示包含正在执行的任务", coordinator.BusyMessage(root).Contains("导入"), null);
        using var other = coordinator.TryEnter(Path.Combine(root, "other"), "下载");
        check("不同游戏根目录允许并行", other is not null, null);
        first.Dispose();
        using var second = coordinator.TryEnter(root, "更新");
        check("结束后可以立即取得写权限", second is not null, null);
        first.Dispose();
        check("重复释放旧任务不会释放新任务", coordinator.TryEnter(root, "第三个") is null, null);
    }

    private static async Task UpdatesAsync(string root, Action<string, bool, string?> check)
    {
        foreach (var mode in new[] { "success", "partial", "cancel", "corrupt", "missing-dependency", "locked-old", "same-name", "disabled", "cf", "pinned-dependency", "missing-pinned", "installed-pinned-conflict" })
        {
            var game = Path.Combine(root, mode);
            var mods = Path.Combine(game, "instances", "test", ".minecraft", "mods"); Directory.CreateDirectory(mods);
            var oldName = mode == "disabled" ? "old.jar.disabled" : "old.jar";
            var old = Jar("internal_mod_id", "1.0.0");
            File.WriteAllBytes(Path.Combine(mods, oldName), old);
            var oldOrigin = new ModOrigin(mode == "cf" ? "curseforge" : "modrinth", mode == "cf" ? "777" : "opaque-project", "v1", "1.0.0", Hash(old), old.Length);
            await ModSourceStore.SaveAsync(mods, new Dictionary<string, ModOrigin> { [oldName] = oldOrigin });
            if (mode == "installed-pinned-conflict") File.WriteAllBytes(Path.Combine(mods, "dependency.jar"), Jar("dependency_id", "0.1.0"));
            var before = Snapshot(mods);
            var latestName = mode == "same-name" ? oldName : "new.jar";
            var next = Jar("internal_mod_id", "2.0.0"); var dependency = Jar("dependency_id", "1.0.0");
            var download = new FilesDownloader(new() { [latestName] = next, ["dependency.jar"] = dependency }) { Mode = mode };
            var mr = new Client();
            var selected = Version("opaque-project", "v2", "2.0.0", latestName, next,
                [new("dependency-project", mode is "pinned-dependency" or "installed-pinned-conflict" ? "dep-v1" : mode == "missing-pinned" ? "does-not-exist" : null, "required")]);
            mr.Versions["opaque-project"] = [Version("opaque-project", "v3", "3.0.0", "unexpected.jar", next), selected];
            mr.Versions["dependency-project"] = mode == "missing-dependency" ? [] : [Version("dependency-project", "dep-v1", "1.0.0", "dependency.jar", dependency)];
            if (mode == "pinned-dependency") mr.Versions["dependency-project"] = [Version("dependency-project", "dep-v2", "2.0.0", "unexpected-dependency.jar", dependency), mr.Versions["dependency-project"][0]];
            var cf = new CfClient(new() { [777] = [new CfFile { Id = 22, ModId = 777, FileName = latestName, DisplayName = "Mod 2.0.0", FileLength = next.Length, DownloadUrl = "https://example.invalid/" + latestName, Hashes = [new CfHash { Algo = 1, Value = Hash(next) }], Dependencies = [new CfDependency { ModId = 888, RelationType = 3 }] }],
                [888] = [new CfFile { Id = 23, ModId = 888, FileName = "dependency.jar", FileLength = dependency.Length, DownloadUrl = "https://example.invalid/dependency.jar", Hashes = [new CfHash { Algo = 1, Value = Hash(dependency) }] }] });
            var service = new ModUpdateInstaller(new(download, mr), new(download, cf), download);
            var candidate = new ModUpdateCandidate("internal_mod_id", oldName, "1.0.0", "2.0.0", true, "unused", next.Length, oldOrigin.Source)
                { ProjectId = oldOrigin.ProjectId, VersionId = mode == "cf" ? "22" : "v2", CurrentSha1 = Hash(old) };
            using var cts = new CancellationTokenSource();
            if (mode == "cancel") download.AfterFirst = cts.Cancel;
            FileStream? locked = null;
            if (mode == "locked-old") locked = new FileStream(Path.Combine(mods, oldName), FileMode.Open, FileAccess.Read, FileShare.Read);
            Exception? failure = null;
            ModInstallReport? report = null;
            try { report = await service.UpdateAsync(game, "test", candidate, ct: cts.Token); }
            catch (Exception ex) { failure = ex; }
            finally { locked?.Dispose(); }
            if (mode is "success" or "same-name" or "disabled" or "cf" or "pinned-dependency")
            {
                var expected = latestName + (mode == "disabled" ? ".disabled" : "");
                check($"更新 {mode} 成功且新文件内容正确", failure is null && report?.FileName == expected && File.ReadAllBytes(Path.Combine(mods, expected)).SequenceEqual(next), failure?.ToString());
                check($"更新 {mode} 保留前置并移除旧主文件", File.Exists(Path.Combine(mods, "dependency.jar")) && (oldName == expected || !File.Exists(Path.Combine(mods, oldName))), null);
                var origin = ModSourceStore.Load(mods).GetValueOrDefault(expected);
                check($"更新 {mode} 提交正确平台文件来源", origin is not null && origin.ProjectId == oldOrigin.ProjectId && origin.VersionId == candidate.VersionId && origin.Sha1 == Hash(next), null);
                if (mode == "disabled") check("更新不会重新启用已禁用模组", !File.Exists(Path.Combine(mods, latestName)), null);
                if (mode == "cf") check("CF 更新不会走 Modrinth 安装", mr.Requests.Count == 0, null);
            }
            else
            {
                check($"更新 {mode} 明确返回失败或取消", failure is not null, null);
                check($"更新 {mode} 原模组及来源索引完整保留", SameSnapshot(before, Snapshot(mods)), failure?.Message);
                if (mode is "missing-dependency" or "missing-pinned" or "installed-pinned-conflict") check("缺失前置时不开始下载", download.Calls == 0, null);
                if (mode == "cancel") check("更新取消保持取消异常语义", failure is OperationCanceledException, failure?.GetType().Name);
            }
            var stage = Path.Combine(game, "instances", "test", ".launcher-staging");
            check($"更新 {mode} 清理暂存文件", !Directory.Exists(stage) || !Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories).Any(), null);
        }
    }

    private static async Task SourcesAsync(string root, Action<string, bool, string?> check)
    {
        var game = Path.Combine(root, "sources"); var mods = Path.Combine(game, "instances", "test", ".minecraft", "mods"); Directory.CreateDirectory(mods);
        var bytes = Jar("internal_different", "1.0.0"); var latest = Jar("internal_different", "2.0.0");
        File.WriteAllBytes(Path.Combine(mods, "manual.jar"), bytes);
        var client = new Client(); client.ByHash[Hash(bytes)] = Version("platform-project", "old-id", "1.0.0", "manual.jar", bytes);
        client.Versions["platform-project"] = [Version("platform-project", "new-id", "2.0.0", "new.jar", latest)];
        var service = new ModUpdateService(client, new CfClient([]));
        var report = await service.CheckAsync(game, "test"); var candidate = report.Candidates.Single();
        check("手工放入的模组按文件哈希确认平台项目", candidate is { HasUpdate: true, ProjectId: "platform-project", VersionId: "new-id" }, null);
        check("不会把内部 modId 当作平台项目 ID", !client.Requests.Contains("internal_different"), null);
        await ModSourceStore.SaveAsync(mods, new Dictionary<string, ModOrigin> { ["manual.jar"] = new("modrinth", "wrong-project", "old", "1.0.0", "wrong-hash", bytes.Length) });
        report = await service.CheckAsync(game, "test");
        check("旧来源哈希不符时重新按实际文件匹配", report.Candidates.Single().ProjectId == "platform-project" && !client.Requests.Contains("wrong-project"), null);
        client.ByHash.Clear(); client.Requests.Clear();
        report = await service.CheckAsync(game, "test");
        check("无法确认来源时标为不可更新而不猜项目", !report.Candidates.Single().Resolved && client.Requests.Count == 0, null);

        var download = new FilesDownloader(new() { ["new.jar"] = latest });
        await new ModInstallService(download, client).InstallAsync(game, "fresh", DownloadCategory.Mod, "platform-project");
        var fresh = ModSourceStore.Load(Path.Combine(game, "instances", "fresh", ".minecraft", "mods"));
        check("普通安装也记录项目和版本来源", fresh.GetValueOrDefault("new.jar") is { ProjectId: "platform-project", VersionId: "new-id" }, null);

        var ordinaryCalls = download.Calls;
        client.Versions["platform-project"] = [Version("platform-project", "later", "3.0.0", "later.jar", latest)];
        await new ModInstallService(download, client).InstallAsync(game, "fresh", DownloadCategory.Mod, "platform-project");
        check("平台 ID 与内部 modId 不同也能按来源去重", download.Calls == ordinaryCalls, null);
        var cfInstall = new CfClient(new() { [777] = [new CfFile { Id = 99, ModId = 777, FileName = "cf-new.jar", FileLength = latest.Length, DownloadUrl = "https://example.invalid/cf-new.jar", Hashes = [new CfHash { Algo = 1, Value = Hash(latest) }] }] });
        var cfDownload = new FilesDownloader(new() { ["cf-new.jar"] = latest });
        await new CurseForgeInstallService(cfDownload, cfInstall).InstallAsync(game, "fresh-cf", DownloadCategory.Mod, 777);
        check("CF 普通安装记录平台文件 ID", ModSourceStore.Load(Path.Combine(game, "instances", "fresh-cf", ".minecraft", "mods")).GetValueOrDefault("cf-new.jar") is { Source: "curseforge", ProjectId: "777", VersionId: "99" }, null);
        var prefixedMods = Path.Combine(game, "instances", "prefixed", ".minecraft", "mods"); Directory.CreateDirectory(prefixedMods);
        File.WriteAllBytes(Path.Combine(prefixedMods, "[装饰] cfmod-old.jar"), Jar("cfmod", "1.0.0"));
        var beforeCalls = cfDownload.Calls;
        await new CurseForgeInstallService(cfDownload, cfInstall).InstallAsync(game, "prefixed", DownloadCategory.Mod, 777);
        check("普通安装保留 CF slug 和中文前缀去重", cfDownload.Calls == beforeCalls, null);
        var packGame = Path.Combine(root, "pack"); var vanilla = Path.Combine(packGame, "versions", "1.20.1", "1.20.1.json"); Directory.CreateDirectory(Path.GetDirectoryName(vanilla)!); File.WriteAllText(vanilla, "{\"id\":\"1.20.1\"}");
        var pack = new ModrinthPack { Name = "source pack", VersionId = "1", SourceLabel = "CurseForge", Dependencies = new Dictionary<string,string> { ["minecraft"] = "1.20.1" }, Files = [new("mods/new.jar", latest.Length, Hash(latest), null, ["https://example.invalid/new.jar"], true) { SourceProjectId = "777", SourceFileId = "22" }] };
        var packDownloader = new FilesDownloader(new() { ["new.jar"] = latest });
        var installer = new ModpackInstaller(new(packDownloader, new StubVersionMeta()), new(new StubLoaderMeta(), new StubVersionMeta(), new(packDownloader, new StubVersionMeta())), new ThrowingForgeInstaller(), new ThrowingNeoForgeInstaller(), packDownloader);
        var imported = await installer.ImportAsync(new FakeModpackSource(pack), packGame);
        var importedOrigins = ModSourceStore.Load(Path.Combine(imported.InstanceDirectory, "mods"));
        check("CF 包导入保留项目与文件来源", importedOrigins.GetValueOrDefault("new.jar") is { Source: "curseforge", ProjectId: "777", VersionId: "22" }, null);
    }

    private static async Task CacheAsync(string root, Action<string, bool, string?> check)
    {
        var requests = 0;
        using var handler = new HttpFixture(async (request, ct) =>
        {
            Interlocked.Increment(ref requests); await Task.Delay(25, ct);
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"latest\":{\"release\":\"test\"},\"versions\":[]}") };
        });
        using var http = new HttpClient(handler);
        var cache = Path.Combine(root, "cache");
        var meta = new MojangMetaClient(http, cacheDirectory: cache);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => meta.GetManifestAsync()));
        check("并发获取相同元数据只请求一次", requests == 1, requests.ToString());
        check("缓存写入不会留下临时文件", !Directory.GetFiles(cache, "*.tmp").Any(), null);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        check("缓存命中仍尊重用户取消", await Throws<OperationCanceledException>(() => meta.GetManifestAsync(canceled.Token)), null);
        var diskOnly = new MojangMetaClient(http, cacheDirectory: cache);
        await diskOnly.GetManifestAsync(); check("新客户端复用磁盘缓存", requests == 1, null);
        using (var locked = new FileStream(Path.Combine(cache, "manifest.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await new MojangMetaClient(http, cacheDirectory: cache).GetManifestAsync();
        check("缓存文件被占用时回退在线获取", requests == 2, null);
        var unusable = Path.Combine(root, "file-not-directory"); File.WriteAllText(unusable, "file");
        await new MojangMetaClient(http, cacheDirectory: unusable).GetManifestAsync();
        check("缓存目录不可写不影响在线结果", requests == 3, null);
        var active = new MojangMetaClient(http);
        var first = active.GetManifestAsync();
        using var waitCancel = new CancellationTokenSource(5);
        check("取消等待者不会取消另一个请求", await Throws<OperationCanceledException>(() => active.GetManifestAsync(waitCancel.Token)), null);
        await first;
        check("等待者取消后完成结果仍可复用", (await active.GetManifestAsync()).Latest?.Release == "test", null);

        var attempts = 0;
        using var failureHandler = new HttpFixture((request, ct) => Task.FromResult(new HttpResponseMessage(Interlocked.Increment(ref attempts) == 1 ? HttpStatusCode.InternalServerError : HttpStatusCode.OK) { Content = new StringContent("{\"versions\":[]}") }));
        using var failureHttp = new HttpClient(failureHandler);
        var retryClient = new MojangMetaClient(failureHttp);
        check("元数据请求失败正常返回异常", await Throws<HttpRequestException>(() => retryClient.GetManifestAsync()), null);
        await retryClient.GetManifestAsync(); await retryClient.GetManifestAsync();
        check("元数据失败结果不污染后续缓存", attempts == 2, attempts.ToString());
        var beforeAssets = requests;
        await Task.WhenAll(Enumerable.Range(0,8).Select(_ => meta.GetAssetIndexAsync(new AssetIndexRef { Id="one-index", Url="https://example.invalid/index" })));
        check("资源索引也合并相同键的并发请求", requests == beforeAssets + 1, null);
        var jar = Path.Combine(root, "changing.jar"); File.WriteAllBytes(jar, Jar("cachemod", "1.0.0"));
        check("首次元数据扫描得到旧版本", ModFileGuard.ReadMetadata(jar)?.Version == "1.0.0", null);
        File.WriteAllBytes(jar, Jar("cachemod", "2.0.0-longer"));
        check("同一路径修改后刷新元数据缓存", ModFileGuard.ReadMetadata(jar)?.Version == "2.0.0-longer", null);
        File.Delete(jar);
        check("扫描与删除交错时返回空元数据", ModFileGuard.ReadMetadata(jar) is null, null);
        File.WriteAllBytes(jar, Jar("cachemod", "3.0.0"));
        using (var locked = new FileStream(jar, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            check("临时无法读取不会崩溃", ModFileGuard.ReadMetadata(jar) is null, null);
        check("临时读取失败不缓存成永久空结果", ModFileGuard.ReadMetadata(jar)?.Version == "3.0.0", null);

        var apiBody = JsonSerializer.Serialize(Version("matched", "matched-version", "1.0", "f.jar", [1,2]));
        string? requested = null;
        using var apiHandler = new HttpFixture((request, ct) => { requested = request.RequestUri!.PathAndQuery; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(apiBody) }); });
        using var apiHttp = new HttpClient(apiHandler) { BaseAddress = new Uri("https://example.invalid/v2/") };
        check("Modrinth 客户端使用文件哈希匹配端点", (await new ModrinthClient(apiHttp).GetVersionByHashAsync(new string('a',40)))?.ProjectId == "matched" && requested!.Contains("version_file/") && requested.Contains("algorithm=sha1"), null);
    }

    private static async Task LaunchAsync(string root, Action<string, bool, string?> check)
    {
        var version = new VersionJson { Id="test", MainClass="test.Main" };
        var context = new LaunchContext("Tester", "uuid", null, "legacy", "test", "1", root, root, root, Path.Combine(root,"client.jar"), 2048, null, null, 854, 480, false);
        var prep = await LaunchPreparer.PrepareAsync(version, PlatformContext.Current, context, root);
        var direct = LaunchPlanBuilder.Build(version, PlatformContext.Current, prep.Context, "java");
        var reused = LaunchPlanBuilder.Build(version, PlatformContext.Current, prep.Context, "java", prep.ClassPath);
        check("共享 classpath 不改变启动参数", direct.ToArgFileContent() == reused.ToArgFileContent(), null);
        var regular = await LaunchPreflight.CheckAsync(version, PlatformContext.Current, prep.Context);
        var cached = await LaunchPreflight.CheckAsync(version, PlatformContext.Current, prep.Context, preparedClassPath: prep.ClassPath);
        check("共享 classpath 不改变预检结果", regular.Describe() == cached.Describe() && prep.ClassPath is not null, null);
    }

    private static ModrinthVersion Version(string project, string id, string version, string filename, byte[] bytes, IReadOnlyList<ModrinthDependency>? dependencies = null) =>
        new(id, project, project, version, ["1.20.1"], ["fabric"], null, [new("https://example.invalid/"+filename, filename, bytes.Length, true, new(Hash(bytes), null))], dependencies);
    private static byte[] Jar(string id,string version)
    {
        using var memory = new MemoryStream(); using (var zip = new ZipArchive(memory, ZipArchiveMode.Create,true))
        using (var writer = new StreamWriter(zip.CreateEntry("fabric.mod.json").Open())) writer.Write(JsonSerializer.Serialize(new { id, version }));
        return memory.ToArray();
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(bytes));
    private static Dictionary<string,string> Snapshot(string directory) => Directory.GetFiles(directory).ToDictionary(file => Path.GetFileName(file)!, file => Hash(File.ReadAllBytes(file)));
    private static bool SameSnapshot(Dictionary<string,string> first, Dictionary<string,string> next) => first.Count == next.Count && first.All(pair => next.GetValueOrDefault(pair.Key) == pair.Value);
    private static async Task<bool> Throws<T>(Func<Task> action) where T:Exception { try { await action(); return false; } catch(T) { return true; } }

    private sealed class Client : IModrinthClient
    {
        public Dictionary<string,IReadOnlyList<ModrinthVersion>> Versions = [];
        public Dictionary<string,ModrinthVersion> ByHash = [];
        public List<string> Requests = [];
        public Task<ModrinthVersion?> GetVersionByHashAsync(string sha1,CancellationToken ct=default) => Task.FromResult(ByHash.GetValueOrDefault(sha1));
        public Task<IReadOnlyList<ModrinthVersion>> GetVersionsAsync(string projectIdOrSlug,string? gameVersion=null,string? loader=null,CancellationToken ct=default) { ct.ThrowIfCancellationRequested(); Requests.Add(projectIdOrSlug); return Task.FromResult(Versions.GetValueOrDefault(projectIdOrSlug) ?? (IReadOnlyList<ModrinthVersion>)[]); }
        public Task<ModrinthSearchResponse> SearchAsync(string query,DownloadCategory category,string? gameVersion=null,string? loader=null,int limit=20,int offset=0,CancellationToken ct=default) => throw new NotSupportedException();
    }
    private sealed class CfClient(Dictionary<int,IReadOnlyList<CfFile>> files) : ICurseForgeClient
    {
        public bool IsConfigured => true;
        public Task<IReadOnlyList<CfFile>> GetFilesAsync(int modId,string? gameVersion=null,string? loaderSlug=null,CancellationToken ct=default) => Task.FromResult(files.GetValueOrDefault(modId) ?? (IReadOnlyList<CfFile>)[]);
        public Task<CfMod?> GetModAsync(int modId,CancellationToken ct=default) => Task.FromResult<CfMod?>(new CfMod { Id=modId,Name="CF",Slug="cfmod" });
        public Task<string?> GetDownloadUrlAsync(int modId,int fileId,CancellationToken ct=default) => Task.FromResult<string?>(null);
        public CfUrlRef? ParseUrl(string url) => null;
        public Task<CfSearchResponse> SearchAsync(string query,DownloadCategory category,string? gameVersion=null,string? loaderSlug=null,int pageSize=20,int index=0,CancellationToken ct=default) => throw new NotSupportedException();
    }
    private sealed class FilesDownloader(Dictionary<string,byte[]> payloads) : IDownloader
    {
        public string Mode=""; public int Calls; public Action? AfterFirst;
        public async Task<DownloadSummary> DownloadAsync(IReadOnlyList<DownloadEntry> entries,string rootDirectory,IProgress<DownloadProgress>? progress=null,CancellationToken ct=default)
        {
            Calls++; var index=0;
            foreach(var entry in entries)
            {
                ct.ThrowIfCancellationRequested(); var filename=Path.GetFileName(new Uri(entry.Url).LocalPath);
                var bytes=Mode=="corrupt" ? new byte[entry.Size] : payloads[filename];
                var path=LongPath.Combine(rootDirectory,entry.RelativePath); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path,bytes,ct);
                if(index++==0) { AfterFirst?.Invoke(); if(Mode=="partial") throw new IOException("dependency download failed"); }
            }
            ct.ThrowIfCancellationRequested(); return new(entries.Count,entries.Count,0,entries.Sum(e=>e.Size),TimeSpan.Zero);
        }
    }
    private sealed class HttpFixture(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> action) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) => action(request,ct); }
}
