using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Launcher.Application.Abstractions;
using Launcher.Application.Export;
using Launcher.Application.Install;
using Launcher.Application.Update;
using Launcher.Domain.Install;
using Launcher.Infrastructure.Http;
using Launcher.Infrastructure.Java;

internal static class OptimizationChecks
{
    public static bool IsJavaFixture(string[] args) => args is ["-version"] && Environment.GetEnvironmentVariable("MCL_CHECK_JAVA_FIXTURE") is not null;

    public static int RunJavaFixture()
    {
        var state = Environment.GetEnvironmentVariable("MCL_CHECK_JAVA_FIXTURE")!;
        File.AppendAllText(state + ".calls", "probe\n");
        if (File.ReadAllText(state) == "hang") Thread.Sleep(10000);
        else Thread.Sleep(150);
        if (File.ReadAllText(state) == "java8") { Console.Error.WriteLine("java version \"1.8.0_421\""); return 0; }
        Console.Error.WriteLine("openjdk version \"21.0.1\" 64-Bit Server VM");
        return 0;
    }

    public static async Task RunAsync(Action<string, bool, string?> check)
    {
        Console.WriteLine("== 30. 下载、探测、导出和更新故障回归 ==");
        var root = Path.Combine(Path.GetTempPath(), "mcl-opt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await DownloadsAsync(Path.Combine(root, "downloads"), check);
            await JavaAsync(Path.Combine(root, "java"), check);
            await ExportAsync(Path.Combine(root, "export"), check);
            ImportRollback(Path.Combine(root, "import"), check);
            await UpdateAsync(Path.Combine(root, "update"), check);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task DownloadsAsync(string root, Action<string, bool, string?> check)
    {
        Directory.CreateDirectory(root);
        var body = Encoding.UTF8.GetBytes("verified-content");
        var sha = Convert.ToHexStringLower(SHA1.HashData(body));
        var entry = new DownloadEntry("https://example.invalid/file", "file.jar", body.Length, sha, DownloadKind.Library);
        var reports = new List<DownloadProgress>();
        using var handler = new Handler((request, index) => Task.FromResult(Ok(body)));
        using var http = new HttpClient(handler);
        var downloader = new HttpDownloader(http, new DownloadOptions(MaxConcurrency: 1));
        File.WriteAllBytes(Path.Combine(root, "skip.jar"), body);
        var report = await downloader.DownloadAsync([entry with { RelativePath = "skip.jar" }, entry], root, new Immediate<DownloadProgress>(reports.Add));
        check("混合跳过与下载时完成文件数正确", reports[^1].CompletedFiles == 2 && reports[^1].Percent == 100, null);
        check("跳过文件不计入下载量", report.DownloadedBytes == body.Length && report.SkippedFiles == 1 && report.DownloadedFiles == 1, null);
        check("已有有效文件不发 HTTP 请求", handler.Calls == 1, null);

        File.WriteAllBytes(Path.Combine(root, "complete.jar.part"), body);
        await downloader.DownloadAsync([entry with { RelativePath = "complete.jar" }], root);
        check("完整且通过校验的断点直接就位，不请求 Range", handler.Calls == 1 && File.ReadAllBytes(Path.Combine(root, "complete.jar")).SequenceEqual(body), null);
        File.WriteAllBytes(Path.Combine(root, "bad.jar.part"), new byte[body.Length]);
        await downloader.DownloadAsync([entry with { RelativePath = "bad.jar" }], root);
        check("完整但校验失败的断点重新下载", handler.Calls == 2 && File.ReadAllBytes(Path.Combine(root, "bad.jar")).SequenceEqual(body), null);

        // Cancel during the body copy, after the first bytes have reached a .part file.
        using(var cancel=new CancellationTokenSource())
        using(var cancelHandler=new Handler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(new InterruptedBody(cancel))})))
        using(var cancelHttp=new HttpClient(cancelHandler))
        {
            bool bodyCanceled=false;
            try {await new HttpDownloader(cancelHttp,new DownloadOptions(MaxConcurrency:1,MaxRetries:1)).DownloadAsync([entry with{RelativePath="cancel.jar"}],root,ct:cancel.Token);}catch(OperationCanceledException){bodyCanceled=true;}
            check("取消传输删除未完成 part 文件",bodyCanceled&&!File.Exists(Path.Combine(root,"cancel.jar.part"))&&!File.Exists(Path.Combine(root,"cancel.jar")),null);
            check("取消传输保留已验证共享文件",File.ReadAllBytes(Path.Combine(root,"file.jar")).SequenceEqual(body),null);
        }
        using(var brokenHandler=new Handler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(new InterruptedBody(null))})))
        using(var brokenHttp=new HttpClient(brokenHandler))
        {
            try {await new HttpDownloader(brokenHttp,new DownloadOptions(MaxConcurrency:1,MaxRetries:1)).DownloadAsync([entry with{RelativePath="network.jar"}],root);}catch(AggregateException){}
            check("普通网络中断保留断点以便续传",File.Exists(Path.Combine(root,"network.jar.part"))&&new FileInfo(Path.Combine(root,"network.jar.part")).Length>0,null);
        }

        foreach (var mode in new[] { "206", "200", "416", "wrong-range" })
        {
            var ranges = new List<long?>();
            File.WriteAllBytes(Path.Combine(root, mode + ".jar.part"), body[..3]);
            using var resumeHandler = new Handler((request, index) =>
            {
                ranges.Add(request.Headers.Range?.Ranges.First().From);
                if (mode == "416" && index == 1) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
                if (mode == "206" || (mode == "wrong-range" && index == 1))
                {
                    var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(body[3..]) };
                    response.Content.Headers.ContentRange = new ContentRangeHeaderValue(mode == "206" ? 3 : 0, body.Length - 1, body.Length);
                    return Task.FromResult(response);
                }
                return Task.FromResult(Ok(body));
            });
            using var resumeHttp = new HttpClient(resumeHandler);
            await new HttpDownloader(resumeHttp, new DownloadOptions(MaxConcurrency: 1, MaxRetries: 2))
                .DownloadAsync([entry with { RelativePath = mode + ".jar" }], root);
            check($"续传 {mode} 最终内容正确", File.ReadAllBytes(Path.Combine(root, mode + ".jar")).SequenceEqual(body), null);
            if (mode is "416" or "wrong-range") check($"续传 {mode} 失败后丢弃无效断点", ranges.SequenceEqual(new long?[] { 3, null }), null);
        }

        var calls = handler.Calls;
        check("下载路径越界不会触发 HTTP 或写出根目录", await ThrowsAsync<AggregateException>(() => downloader.DownloadAsync([entry with { RelativePath = "../outside.jar" }], root)) && handler.Calls == calls && !File.Exists(Path.Combine(root, "../outside.jar")), null);
        check("下载拒绝绝对路径", await ThrowsAsync<AggregateException>(() => downloader.DownloadAsync([entry with { RelativePath = Path.Combine(root, "rooted.jar") }], root)), null);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        check("下载保留用户取消语义", await ThrowsAsync<OperationCanceledException>(() => downloader.DownloadAsync([entry], root, ct: canceled.Token)), null);

        using var concurrencyHandler = new Handler(async (_, _) => { await Task.Delay(20); return Ok(body); });
        using var concurrencyHttp = new HttpClient(concurrencyHandler);
        await new HttpDownloader(concurrencyHttp, new DownloadOptions(MaxConcurrency: 4)).DownloadAsync(
            Enumerable.Range(0, 20).Select(i => entry with { RelativePath = "parallel-" + i }).ToArray(), root);
        check("下载并发上限仍为配置的 4", concurrencyHandler.Peak <= 4 && concurrencyHandler.Peak > 1, concurrencyHandler.Peak.ToString());
    }

    private static async Task JavaAsync(string root, Action<string, bool, string?> check)
    {
        if (!OperatingSystem.IsWindows()) return; // 使用本项目 Windows apphost 作为可计数的真实探测进程。
        var bin = Path.Combine(root, "bin"); Directory.CreateDirectory(bin);
        foreach (var file in Directory.GetFiles(AppContext.BaseDirectory)) File.Copy(file, Path.Combine(bin, Path.GetFileName(file)));
        var executable = Path.Combine(bin, "Core.Checks.exe");
        var state = Path.Combine(root, "state"); File.WriteAllText(state, "normal");
        var previousFixture = Environment.GetEnvironmentVariable("MCL_CHECK_JAVA_FIXTURE");
        var previousRuntime = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("MCL_CHECK_JAVA_FIXTURE", state);
            var runtime = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
            Environment.SetEnvironmentVariable("DOTNET_ROOT", Path.GetFullPath(Path.Combine(runtime, "../../..")));
            var locator = new JavaLocator();
            var stopwatch = Stopwatch.StartNew();
            var cold = await locator.ProbeAsync(executable); var coldMs = stopwatch.Elapsed.TotalMilliseconds;
            stopwatch.Restart(); var warm = await locator.ProbeAsync(executable); var warmMs = stopwatch.Elapsed.TotalMilliseconds;
            check("Java 缓存命中不启动第二个进程", cold?.MajorVersion == 21 && warm == cold && File.ReadAllLines(state + ".calls").Length == 1, null);
            Console.WriteLine($"  [measure] Java 测试进程：首次 {coldMs:F1} ms，缓存 {warmMs:F2} ms（含人工 150 ms 延迟）");
            File.WriteAllText(Path.Combine(root, "release"), "JAVA_VERSION=21");
            await locator.ProbeAsync(executable);
            check("Java release 文件变化使缓存失效", File.ReadAllLines(state + ".calls").Length == 2, null);
            File.SetLastWriteTimeUtc(executable, File.GetLastWriteTimeUtc(executable).AddSeconds(10));
            await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => locator.ProbeAsync(executable)));
            check("同一路径并发探测合并为一个进程", File.ReadAllLines(state + ".calls").Length == 3, null);
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            check("Java 缓存命中也尊重取消", await ThrowsAsync<OperationCanceledException>(() => locator.ProbeAsync(executable, canceled.Token)), null);
            File.WriteAllText(state, "hang");
            using var cancelProbe = new CancellationTokenSource(300);
            check("Java 探测过程中取消向调用方传播", await ThrowsAsync<OperationCanceledException>(() => new JavaLocator().ProbeAsync(executable, cancelProbe.Token)), null);
            File.WriteAllText(state, "normal");
            var invalid = Path.Combine(bin, "invalid.exe"); File.WriteAllText(invalid, "not executable");
            check("损坏的 Java 可执行文件不会击穿扫描", await locator.ProbeAsync(invalid) is null, null);
            var previousPath = Environment.GetEnvironmentVariable("PATH");
            try
            {
                File.Copy(executable, Path.Combine(bin, "java.exe"));
                Environment.SetEnvironmentVariable("PATH", bin + Path.PathSeparator + previousPath);
                File.WriteAllText(state, "java8");
                check("兼容 CLI 的 PATH java 命令", (await locator.ProbeAsync("java"))?.MajorVersion == 8, null);
                var launcher = new Launcher.Infrastructure.Processes.WinProcessLauncher(locator);
                var supports = typeof(Launcher.Infrastructure.Processes.WinProcessLauncher).GetMethod("SupportsArgFile", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                check("复用探测后 Java 8 仍走直接参数", supports.Invoke(launcher, ["java"]) is false, null);
            }
            finally { Environment.SetEnvironmentVariable("PATH", previousPath); }
        }
        finally
        {
            Environment.SetEnvironmentVariable("MCL_CHECK_JAVA_FIXTURE", previousFixture);
            Environment.SetEnvironmentVariable("DOTNET_ROOT", previousRuntime);
        }
    }

    private static async Task ExportAsync(string root, Action<string, bool, string?> check)
    {
        var game = Path.Combine(root, "instances", "test", ".minecraft"); Directory.CreateDirectory(game);
        var source = Path.Combine(game, "options.txt"); File.WriteAllText(source, "keep-options");
        var archive = Path.Combine(root, "backup.zip"); File.WriteAllText(archive, "old-backup");
        var service = new InstanceExportService();
        using var canceled = new CancellationTokenSource();
        check("取消导出传回取消信号", await ThrowsAsync<OperationCanceledException>(() => service.ExportAsync(root, "test", archive,
            progress: new Immediate<string>(_ => canceled.Cancel()), ct: canceled.Token)), null);
        check("取消导出保留原备份", File.ReadAllText(archive) == "old-backup", null);
        check("取消导出清理临时文件", !Directory.EnumerateFiles(root, "*.part", SearchOption.AllDirectories).Any(), null);
        using (var locked = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            check("导出读文件失败保留原备份", await ThrowsAsync<IOException>(() => service.ExportAsync(root, "test", archive)) && File.ReadAllText(archive) == "old-backup", null);
        var result = await service.ExportAsync(root, "test", archive);
        using (var zip = ZipFile.OpenRead(archive))
            check("成功导出才替换原备份且保留清单", zip.GetEntry(".minecraft/options.txt") is not null && zip.GetEntry(InstanceExportService.ManifestEntryName) is not null && result.FileCount == 1, null);
        var self = Path.Combine(game, "self.zip"); File.WriteAllText(self, "previous-self");
        await service.ExportAsync(root, "test", self);
        using (var zip = ZipFile.OpenRead(self))
            check("导出到实例内不会把自身归档再打包", zip.GetEntry(".minecraft/self.zip") is null && !zip.Entries.Any(e => e.FullName.EndsWith(".part")), null);
    }

    private static void ImportRollback(string root, Action<string, bool, string?> check)
    {
        Directory.CreateDirectory(root);
        var versions = Path.Combine(root, "versions"); File.WriteAllText(versions, "unreadable-directory-fixture");
        var transaction = ImportTransaction.Begin(root);
        File.Delete(versions); var kept = Path.Combine(versions, "keep"); Directory.CreateDirectory(kept); File.WriteAllText(Path.Combine(kept, "save"), "keep");
        var added = Path.Combine(root, "instances", "new"); Directory.CreateDirectory(added);
        transaction.Rollback();
        check("无法读取原目录快照时不猜测并删除", File.Exists(Path.Combine(kept, "save")), null);
        check("快照明确为空的另一目录仍可回滚", !Directory.Exists(added), null);
    }

    private static async Task UpdateAsync(string root, Action<string, bool, string?> check)
    {
        var stage = Path.Combine(root, "stage"); var target = Path.Combine(root, "target"); var backup = Path.Combine(root, "backup");
        Directory.CreateDirectory(stage); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(stage, "existing.txt"), "new"); File.WriteAllText(Path.Combine(target, "existing.txt"), "old");
        File.WriteAllText(Path.Combine(stage, "new.txt"), "new-file"); File.WriteAllText(Path.Combine(stage, "blocked.txt"), "blocked");
        Directory.CreateDirectory(Path.Combine(target, "blocked.txt"));
        var files = new[] { FileOf("existing.txt"), FileOf("new.txt"), FileOf("blocked.txt") };
        var logs = new List<string>();
        check("后续文件替换失败时返回失败", await ThrowsFileErrorAsync(() => UpdateFileTransaction.ApplyAsync(files, stage, target, backup, log: logs.Add)), null);
        check("更新失败恢复已替换的旧文件", File.ReadAllText(Path.Combine(target, "existing.txt")) == "old", null);
        check("更新失败移除本次新增文件", !File.Exists(Path.Combine(target, "new.txt")), null);
        check("更新失败保留备份与暂存文件", File.ReadAllText(Path.Combine(backup, "existing.txt")) == "old" && File.Exists(Path.Combine(stage, "existing.txt")), null);
        check("更新失败清理临时替换文件", !Directory.EnumerateFiles(target, "*.mcl-update-*", SearchOption.AllDirectories).Any(), null);
        Directory.Delete(Path.Combine(target, "blocked.txt"));
        await UpdateFileTransaction.ApplyAsync(files, stage, target, backup + "-success");
        check("更新成功提交全部文件", File.ReadAllText(Path.Combine(target, "existing.txt")) == "new" && File.Exists(Path.Combine(target, "new.txt")) && File.Exists(Path.Combine(target, "blocked.txt")), null);
        using var canceled = new CancellationTokenSource();
        check("更新中途取消仍执行回滚", await ThrowsAsync<OperationCanceledException>(() => UpdateFileTransaction.ApplyAsync(files, stage, target, backup + "-cancel",
            log: text => { if (text.StartsWith("替换")) canceled.Cancel(); }, ct: canceled.Token)) && File.ReadAllText(Path.Combine(target, "existing.txt")) == "new", null);
        File.WriteAllText(Path.Combine(target, "skip.txt"), "unchanged");
        await UpdateFileTransaction.ApplyAsync([FileOf("skip.txt")], stage, target, backup + "-skip");
        check("未暂存但目标已通过调用方校验的文件不替换", File.ReadAllText(Path.Combine(target, "skip.txt")) == "unchanged", null);
        await UpdateFileTransaction.ApplyAsync([FileOf("existing.txt")], stage, target, backup + "-self", Path.Combine(target, "existing.txt"));
        check("运行中的更新器自身仍跳过", !Directory.Exists(backup + "-self"), null);
        if (OperatingSystem.IsWindows())
        {
            File.Delete(Path.Combine(target, "blocked.txt"));
            Directory.CreateDirectory(Path.Combine(target, "blocked.txt"));
            FileStream? locked = null;
            try
            {
                var rollbackLogs = new List<string>();
                check("回滚也被占用时明确报告失败和备份位置", await ThrowsAsync<AggregateException>(() => UpdateFileTransaction.ApplyAsync(files, stage, target, backup + "-rollback-failed",
                    log: text =>
                    {
                        rollbackLogs.Add(text);
                        if (text == "替换 existing.txt") locked = new FileStream(Path.Combine(target, "existing.txt"), FileMode.Open, FileAccess.Read, FileShare.Read);
                    })), null);
                check("回滚失败仍留下原文件备份及日志说明", File.Exists(Path.Combine(backup + "-rollback-failed", "existing.txt")) && rollbackLogs.Any(t => t.StartsWith("回滚失败")), null);
            }
            finally { locked?.Dispose(); }
        }
        foreach (var path in new[] { "\\outside.exe", "/outside.exe", "../outside.exe", "a/./b.exe" })
            check($"更新清单拒绝路径 {path}", Throws<UpdateManifestException>(() => UpdateManifestParser.Parse(Manifest(path))), null);
        check("更新清单拒绝大小写和分隔符重复目标", Throws<UpdateManifestException>(() => UpdateManifestParser.Parse(Manifest("folder/a.exe", "FOLDER\\A.EXE"))), null);
        check("更新清单兼容正常反斜杠相对路径", UpdateManifestParser.Parse(Manifest("folder\\a.exe")).Files[0].Path == "folder/a.exe", null);
    }

    private static string Manifest(params string[] paths) => JsonSerializer.Serialize(new { version = "0.2.0", files = paths.Select(path => new { path, url = "file", sha256 = "", size = 1 }) });
    private static UpdateFile FileOf(string path) => new(path, "unused", "", 0);
    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
    private static async Task<bool> ThrowsAsync<T>(Func<Task> run) where T : Exception
    { try { await run(); return false; } catch (T) { return true; } }
    private static async Task<bool> ThrowsFileErrorAsync(Func<Task> run)
    { try { await run(); return false; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; } }
    private static bool Throws<T>(Action run) where T : Exception
    { try { run(); return false; } catch (T) { return true; } }
    private sealed class Immediate<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
    private sealed class Handler(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls; public int Peak; private int _active;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var current = Interlocked.Increment(ref _active);
            int peak;
            do { peak = Volatile.Read(ref Peak); } while (current > peak && Interlocked.CompareExchange(ref Peak, current, peak) != peak);
            try { ct.ThrowIfCancellationRequested(); return await send(request, Interlocked.Increment(ref Calls)); }
            finally { Interlocked.Decrement(ref _active); }
        }
    }
}

internal sealed class InterruptedBody(CancellationTokenSource? cancellation):Stream
{
    private bool sent;
    public override bool CanRead=>true;
    public override bool CanSeek=>false;
    public override bool CanWrite=>false;
    public override long Length=>throw new NotSupportedException();
    public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
    public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct=default)
    {
        if(!sent) {sent=true;buffer.Span[0]=42;return ValueTask.FromResult(1);}
        if(cancellation is not null){cancellation.Cancel();ct.ThrowIfCancellationRequested();}
        throw new IOException("fixture disconnected");
    }
    public override int Read(byte[] buffer,int offset,int count)=>ReadAsync(buffer.AsMemory(offset,count)).GetAwaiter().GetResult();
    public override void Flush(){}
    public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
    public override void SetLength(long value)=>throw new NotSupportedException();
    public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
}
