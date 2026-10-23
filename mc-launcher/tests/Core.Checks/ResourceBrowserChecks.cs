using System.Net;
using System.Text;
using System.Text.Json;
using Launcher.Application.Instances;
using Launcher.Application.Install;
using Launcher.Application.Mods;
using Launcher.Domain.Content;
using Launcher.Domain.Versions;
using Launcher.Infrastructure.Modrinth;
using Launcher.Infrastructure.CurseForge;

internal static class ResourceBrowserChecks
{
    public static async Task RunAsync(Action<string, bool, string?> check)
    {
        foreach (var id in new[] { "15w14a", "1.RV-Pre1", "3D Shareware v1.34", "20w14infinite", "22w13oneblockatatime", "23w13a_or_b", "24w14potato", "25w14craftmine", "26w14a" })
            check("官方活动版独立分组 " + id, VersionClassification.Group(id, "snapshot") == "活动版", null);
        check("普通预览版不按日期误归活动版", VersionClassification.Group("26.4-snapshot-4", "snapshot") == "预览版", null);
        check("两个远古组独立", VersionClassification.Group("b1.7.3", "old_beta") != VersionClassification.Group("a1.2.6", "old_alpha"), null);
        var handler = new Handler();
        using var http = new HttpClient(handler) { BaseAddress = new("https://fixture.test/") };
        var mr = new ModrinthClient(http); var cf = new CurseForgeClient(http, () => "fixture-key");
        foreach (var category in new[] { DownloadCategory.Mod, DownloadCategory.Modpack, DownloadCategory.ResourcePack, DownloadCategory.Datapack, DownloadCategory.Shader })
        {
            await mr.SearchAsync(new ResourceSearchRequest("中文 & test", category, "1.21.4", "fabric", "adventure", ResourceSort.Downloads, 20, 40));
            var query = Query(handler.Last!);
            var facets = JsonSerializer.Deserialize<string[][]>(query["facets"])!.SelectMany(v => v).ToArray();
            check("MR 类型正确 " + category, facets.Contains(category == DownloadCategory.Datapack ? "all_project_types:datapack" : "project_type:" + category.ModrinthProjectType()), null);
            check("MR 服务端筛选排序分页 " + category, query["index"] == "downloads" && query["offset"] == "40" && query["query"] == "中文 & test" && facets.Contains("versions:1.21.4") && facets.Contains("categories:adventure") && facets.Contains("categories:fabric") == category.UsesLoader(), null);
            await cf.SearchAsync(new ResourceSearchRequest("", category, "1.21.4", "fabric", "42", ResourceSort.Updated, 20, 60));
            query = Query(handler.Last!);
            check("CF 分类版本排序分页 " + category, query["classId"] == category.CurseForgeClassId().ToString() && query["categoryId"] == "42" && query["sortField"] == "3" && query["index"] == "60" && query["gameVersion"] == "1.21.4" && query.ContainsKey("modLoaderType") == category.UsesLoader(), null);
        }
        foreach (var (sort, index, field) in new[] { (ResourceSort.Relevance,"relevance","2"), (ResourceSort.Popularity,"follows","2"), (ResourceSort.Downloads,"downloads","6"), (ResourceSort.Updated,"updated","3"), (ResourceSort.Newest,"newest","11") })
        {
            await mr.SearchAsync(new ResourceSearchRequest("", DownloadCategory.Mod, Sort: sort));
            check("MR 服务端排序 " + sort, Query(handler.Last!)["index"] == index, null);
            await cf.SearchAsync(new ResourceSearchRequest("", DownloadCategory.Mod, Sort: sort));
            check("CF 服务端排序 " + sort, Query(handler.Last!)["sortField"] == field, null);
        }
        await mr.SearchAsync(new ResourceSearchRequest("", DownloadCategory.Mod, "1.21\"quoted"));
        check("手输版本 JSON 正确转义", JsonSerializer.Deserialize<string[][]>(Query(handler.Last!)["facets"])!.Any(g => g.Contains("versions:1.21\"quoted")), null);
        handler.CategoryResponse = true;
        var tags = await mr.GetCategoriesAsync(DownloadCategory.Mod);
        check("MR 分类依资源类型区分", tags.Count == 1 && tags[0].Id == "adventure", null);
        tags = await mr.GetCategoriesAsync(DownloadCategory.Shader);
        check("MR 光影分类来自光影标签", tags.Single().Id == "realistic", null);
        tags = await cf.GetCategoriesAsync(DownloadCategory.Mod);
        check("CF 分类使用平台数字 ID", tags.Single().Id == "42", null);
        handler.CategoryResponse = false; handler.Forbidden = true;
        var denied = false;
        try { await cf.SearchAsync(new ResourceSearchRequest("", DownloadCategory.Mod)); } catch (CurseForgeSearchUnavailableException) { denied = true; }
        check("CF 搜索无权限提供明确异常", denied, null);
        handler.Forbidden = false;
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var stopped = false;
        try { await mr.SearchAsync(new ResourceSearchRequest("", DownloadCategory.Mod), canceled.Token); } catch (OperationCanceledException) { stopped = true; }
        check("检索支持取消", stopped, null);
        await InstallationFiltersAsync(mr, cf, handler, check);
        Deletion(check);
    }
    private static async Task InstallationFiltersAsync(IModrinthClient mr, ICurseForgeClient cf, Handler handler, Action<string,bool,string?> check)
    {
        var packs = new ModpackDownloadService(null!, mr, cf, null!, null!);
        try { await packs.InstallAsync("unused", "project", gameVersion:"1.20.1", loader:"fabric"); } catch (InvalidOperationException) { }
        var query = Query(handler.Last!);
        check("MR 整合包安装遵循指定游戏版本与加载器", query["game_versions"] == "[\"1.20.1\"]" && query["loaders"] == "[\"fabric\"]", null);
        try { await packs.InstallCurseForgeAsync("unused", 123, gameVersion:"1.20.1", loader:"fabric"); } catch (InvalidOperationException) { }
        query = Query(handler.Last!);
        check("CF 整合包安装遵循指定游戏版本与加载器", query["gameVersion"] == "1.20.1" && query.ContainsKey("modLoaderType"), null);
        var root = Path.Combine(Path.GetTempPath(), "mcl-datapack-" + Guid.NewGuid().ToString("N"));
        try
        {
            var version = Path.Combine(root, "versions", "1.21.4"); Directory.CreateDirectory(version);
            File.WriteAllText(Path.Combine(version,"1.21.4.json"),"{\"id\":\"1.21.4\"}");
            var installer = new ModInstallService(null!, mr);
            try { await installer.InstallAsync(root, "1.21.4", DownloadCategory.Datapack, "project"); } catch (InvalidOperationException) { }
            query = Query(handler.Last!);
            check("数据包安装只请求 datapack 文件而非同项目模组 JAR", query["loaders"] == "[\"datapack\"]", null);
        }
        finally { Directory.Delete(root, recursive:true); }
    }
    private static void Deletion(Action<string,bool,string?> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "mcl-deletion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var scenario in new[] { "single", "virtual", "shared", "inheritsFrom", "x-launcher-parent" })
            {
                var game = Path.Combine(root, scenario);
                var version = Path.Combine(game, "versions", "v"); Directory.CreateDirectory(version);
                File.WriteAllText(Path.Combine(version, "v.json"), "{\"id\":\"v\"}");
                var natives = Path.Combine(game, "natives", "v", "hash"); Directory.CreateDirectory(natives);
                File.WriteAllText(Path.Combine(natives, "native.dll"), "fixture");
                foreach (var name in new[] { "assets", "libraries" }) { Directory.CreateDirectory(Path.Combine(game, name)); File.WriteAllText(Path.Combine(game, name, "shared"), "keep"); }
                var instance = Path.Combine(game, "instances", "v");
                if (scenario != "virtual")
                {
                    new InstanceMetadata("first", null, null, null, "v", null, "v").Write(game, "v");
                    Directory.CreateDirectory(Path.Combine(instance, ".minecraft", "saves"));
                    File.WriteAllText(Path.Combine(instance, ".minecraft", "saves", "test"), "private");
                }
                if (scenario == "shared") new InstanceMetadata("second", null, null, null, "v", null, "v").Write(game, "v-2");
                if (scenario is "inheritsFrom" or "x-launcher-parent")
                {
                    var loader = Path.Combine(game, "versions", "loader"); Directory.CreateDirectory(loader);
                    File.WriteAllText(Path.Combine(loader, "loader.json"), "{\"" + scenario + "\":\"v\"}");
                }
                InstanceRegistry.LoadOrMigrate(game);
                var result = InstanceDeletion.Delete(game, "v", "v");
                var keep = scenario is "shared" or "inheritsFrom" or "x-launcher-parent";
                check("删除私有实例数据 " + scenario, !Directory.Exists(instance), null);
                check("版本和 native 按依赖保留 " + scenario, result.VersionRetained == keep && Directory.Exists(version) == keep && Directory.Exists(natives) == keep, null);
                check("共享资源保留 " + scenario, File.Exists(Path.Combine(game, "assets", "shared")) && File.Exists(Path.Combine(game, "libraries", "shared")), null);
                if (!keep) check("重新扫描不会复活已删版本 " + scenario, InstanceRegistry.Load(game).Instances.Count == 0, null);
                if (scenario == "shared")
                {
                    InstanceDeletion.Delete(game, "v-2", "v");
                    check("最后共享实例删除后回收版本文件", !Directory.Exists(version) && !Directory.Exists(natives), null);
                }
            }
            var rejected = false;
            try { InstanceDeletion.Delete(root, "..", "v"); } catch (ArgumentException) { rejected = true; }
            check("非法删除路径被拒绝", rejected, null);
        }
        finally { Directory.Delete(root, true); }
    }
    private static Dictionary<string,string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&').Select(s => s.Split('=',2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
    private sealed class Handler : HttpMessageHandler
    {
        public Uri? Last; public bool CategoryResponse; public bool Forbidden;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Last = request.RequestUri;
            var body = CategoryResponse ? Last!.AbsolutePath.Contains("tag/") ?
                "[{\"name\":\"adventure\",\"project_type\":\"mod\",\"header\":\"categories\"},{\"name\":\"realistic\",\"project_type\":\"shader\",\"header\":\"categories\"}]" : "{\"data\":[{\"id\":42,\"name\":\"Adventure\"}]}" :
                "{\"hits\":[],\"total_hits\":0,\"offset\":0,\"limit\":20,\"data\":[]}";
            if (Last!.AbsolutePath.EndsWith("/version")) body = "[]";
            return Task.FromResult(new HttpResponseMessage(Forbidden ? HttpStatusCode.Forbidden : HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8,"application/json") });
        }
    }
}
