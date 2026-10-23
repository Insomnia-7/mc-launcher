using System.Diagnostics;
using Launcher.Application.Versions;
using Launcher.Domain.Install;
using Launcher.Domain.Versions;
using Launcher.Infrastructure.Java;
using Launcher.Infrastructure.Meta;

// M1 验证器：不启动游戏，只回答"这个版本要下多少、能不能启动、Java 够不够"。
// 用法： probe [版本号|latest|snapshot] [--mirror] [--all]

var requested = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "latest";
if (string.IsNullOrWhiteSpace(requested)) requested = "latest";
var useMirror = args.Contains("--mirror");
var showAllJava = args.Contains("--all");

var cacheDir = Path.Combine(Environment.CurrentDirectory, ".probe-cache");
using var http = MojangMetaClient.CreateHttpClient("LauncherProbe", "0.1.0");
var meta = new MojangMetaClient(http, useMirror, cacheDir);
var resolver = new VersionResolver(meta);

using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
var ct = cts.Token;

try
{
    // ── 1. 版本清单 ────────────────────────────────────────────────
    var manifest = await meta.GetManifestAsync(ct);
    var releases = manifest.Versions.Count(v => v.Type == "release");
    var snapshots = manifest.Versions.Count(v => v.Type == "snapshot");
    var others = manifest.Versions.Count - releases - snapshots;

    Section("游戏版本清单");
    Row("最新正式版", manifest.Latest?.Release ?? "?");
    Row("最新快照", manifest.Latest?.Snapshot ?? "?");
    Row("清单条目", $"{manifest.Versions.Count:N0}（release {releases} / snapshot {snapshots} / 其他 {others}）");
    Row("下载源", useMirror ? "BMCLAPI 镜像" : "官方");

    var versionId = requested switch
    {
        "latest" or "release" => manifest.Latest?.Release ?? throw new InvalidOperationException("清单里没有 release"),
        "snapshot" => manifest.Latest?.Snapshot ?? throw new InvalidOperationException("清单里没有 snapshot"),
        _ => requested,
    };

    var entry = manifest.Versions.FirstOrDefault(v => string.Equals(v.Id, versionId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"清单里找不到版本 {versionId}");

    // ── 2. 版本 JSON ───────────────────────────────────────────────
    var resolved = await resolver.ResolveAsync(versionId, ct);
    var ctx = PlatformContext.Current;
    var plan = InstallPlanner.Build(resolved, ctx);
    var excluded = resolved.Libraries.Count - plan.AllowedLibraryCount;

    Section($"版本 {entry.Id}（{entry.Type}，{entry.ReleaseTime:yyyy-MM-dd}）");
    Row("mainClass", resolved.MainClass ?? "?");
    Row("需要 Java", $"{plan.RequiredJavaMajor}（{resolved.JavaVersion?.Component ?? "未标注"}）");
    Row("库条目", $"{resolved.Libraries.Count} 原始 → {plan.AllowedLibraryCount} 可用 / {excluded} 平台排除 / {plan.SkippedLibraryCount} 缺产物");
    Row("资源索引", resolved.AssetIndex is { } ai ? $"{ai.Id}（{FormatSize(ai.TotalSize)}）" : "无");
    Row("平台", $"{ctx.OsName}/{ctx.Arch}");

    // ── 3. 下载计划 ───────────────────────────────────────────────
    Section("下载计划（不含资源对象）");
    PlanRow("client.jar", plan, DownloadKind.Client);
    PlanRow("libraries", plan, DownloadKind.Library);
    PlanRow("natives", plan, DownloadKind.Native);
    PlanRow("asset index", plan, DownloadKind.AssetIndex);
    Console.WriteLine("  " + new string('-', 50));
    Row("合计", $"{plan.Entries.Count} 个文件, {FormatSize(plan.TotalSize)}");

    // ── 4. 资源对象 ───────────────────────────────────────────────
    long assetBytes = 0;
    var assetCount = 0;
    var uniqueHashes = 0;
    if (resolved.AssetIndex is { } indexRef)
    {
        var index = await meta.GetAssetIndexAsync(indexRef, ct);
        assetCount = index.Count;
        assetBytes = index.TotalSize;
        uniqueHashes = index.Objects.Values.Select(o => o.Hash).Distinct(StringComparer.OrdinalIgnoreCase).Count();
    }

    Section("资源对象（assets/objects）");
    Row("对象数", $"{assetCount:N0}");
    Row("唯一 hash", $"{uniqueHashes:N0}");
    Row("总大小", FormatSize(assetBytes));
    Row("全量合计", FormatSize(plan.TotalSize + assetBytes));

    // ── 5. 类路径估算 ─────────────────────────────────────────────
    var gameRoot = @"D:\mc";
    var cpEntries = plan.Entries
        .Where(e => e.Kind is DownloadKind.Library or DownloadKind.Client)
        .Select(e => Path.Combine(gameRoot, e.RelativePath))
        .ToList();
    var cpLength = cpEntries.Sum(p => p.Length) + Math.Max(0, cpEntries.Count - 1);

    Section("类路径估算");
    Row("classpath 条目", cpEntries.Count.ToString());
    Row("classpath 长度", $"{cpLength:N0} 字符");
    Row("判定", cpLength > 30000
        ? "超限风险 → 必须用 argfile"
        : $"安全（上限 32,767，富余 {32767 - cpLength:N0}）");
    if (cpEntries.Count > 0)
        Row("最长一条", Truncate(cpEntries.OrderByDescending(p => p.Length).First(), 56));

    // ── 6. 本机 Java ──────────────────────────────────────────────
    Section("本机 Java");
    var javaLocator = new JavaLocator();
    var sw = Stopwatch.StartNew();
    var javas = await javaLocator.DiscoverAsync(ct);
    sw.Stop();

    if (javas.Count == 0)
    {
        Console.WriteLine("  未发现任何 Java → 需要 JavaProvisioner 自动下载");
    }
    else
    {
        var shown = showAllJava ? javas : javas.Take(6).ToList();
        foreach (var java in shown)
            Console.WriteLine($"  Java {java.MajorVersion,-3} {Pad(java.Vendor, 10)} {Truncate(java.ExecutablePath, 62)}");
        if (shown.Count < javas.Count)
            Console.WriteLine($"  … 另有 {javas.Count - shown.Count} 个（--all 查看）");
        Row("扫描耗时", $"{sw.ElapsedMilliseconds} ms / {javas.Count} 个");
    }

    var chosen = javas.Where(j => j.MajorVersion >= plan.RequiredJavaMajor)
                      .OrderBy(j => j.MajorVersion)
                      .FirstOrDefault();

    Row("选中", chosen is not null
        ? $"Java {chosen.MajorVersion} ({chosen.Vendor}) 满足 ≥{plan.RequiredJavaMajor}"
        : $"无满足 ≥{plan.RequiredJavaMajor} 的 Java → 需要安装");

    Section("结论");
    var ready = chosen is not null && resolved.MainClass is not null && resolved.Downloads?.Client is not null;
    Console.WriteLine(ready
        ? "  解析链路 OK：可以进入下载与启动实现"
        : "  解析链路有缺口：检查 mainClass / client.jar / Java 三项");
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"\n[失败] {ex.GetType().Name}: {ex.Message}");
    Console.ResetColor();
    return 1;
}

return 0;

// ── 输出辅助（中文按 2 列宽计算，否则冒号会错位）─────────────────
static void Section(string title)
{
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"── {title} " + new string('─', Math.Max(0, 52 - DisplayWidth(title) - 3)));
    Console.ResetColor();
}

static void Row(string key, string value) => Console.WriteLine($"  {Pad(key, 16)}: {value}");

static void PlanRow(string label, InstallPlan plan, DownloadKind kind)
{
    var count = plan.CountOf(kind);
    if (count == 0) return;
    Console.WriteLine($"  {Pad(label, 16)}: {count,4} 个   {FormatSize(plan.SizeOf(kind)),10}");
}

static int DisplayWidth(string text) => text.Sum(c => c > 0x7F ? 2 : 1);

static string Pad(string text, int width)
{
    var w = DisplayWidth(text);
    return w >= width ? text : text + new string(' ', width - w);
}

static string Truncate(string text, int max)
    => DisplayWidth(text) <= max ? text : text[..Math.Max(0, max - 3)] + "...";

static string FormatSize(long bytes) => bytes switch
{
    >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:F2} GB",
    >= 1024L * 1024 => $"{bytes / 1024.0 / 1024:F1} MB",
    >= 1024L => $"{bytes / 1024.0:F1} KB",
    _ => $"{bytes} B",
};
