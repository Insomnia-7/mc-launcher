using Launcher.Application.Abstractions;
using Launcher.Application.Install;
using Launcher.Application.Java;
using Launcher.Domain.Install;
using Launcher.Domain.Java;
using Launcher.Domain.Versions;

internal static class FirstRunChecks
{
    public static async Task RunAsync(Action<string, bool, string?> check)
    {
        Console.WriteLine("== 29. 首次安装引导 ==");
        var root = Path.Combine(Path.GetTempPath(), "mcl-first-run-" + Guid.NewGuid().ToString("N"));
        try
        {
            var meta = new SetupMeta();
            var sink = new SetupDownloader();
            var java = new SetupJava();
            var service = new FirstRunInstaller(new GameInstaller(sink, meta), meta, java);
            var reports = new List<FirstRunProgress>();
            await service.InstallAsync(new("setup-test", 3072), root, Path.Combine(root, "data"), new ImmediateProgress(reports.Add));
            check("向导安装包含资源文件", sink.Entries.Any(e => e.Kind == DownloadKind.Asset), null);
            check("向导按版本元数据准备 Java", java.Required == 21, null);
            check("向导保存所选内存", InstanceSettings.Load(root, "setup-test").MemoryMb == 3072, null);
            check("向导进度在所有阶段完成后到达 100%", reports[^1].Percent == 100 && reports.Any(p => p.Percent == 80), null);
            check("向导保存可复用的版本元数据", File.Exists(Path.Combine(root, "versions", "setup-test", "setup-test.json")), null);

            var customized = new InstanceSettings(4096, ExtraJvmArgs: "-Dkeep=yes", WindowWidth: 1920);
            customized.Save(root, "setup-test");
            await service.InstallAsync(new("setup-test", 2048), root, Path.Combine(root, "data"));
            var settings = InstanceSettings.Load(root, "setup-test");
            check("重新安装保留既有窗口和 JVM 参数", settings.ExtraJvmArgs == "-Dkeep=yes" && settings.WindowWidth == 1920 && settings.MemoryMb == 2048, null);

            var before = File.ReadAllText(InstanceSettings.PathFor(root, "setup-test"));
            java.Fail = true;
            var failed = await ThrowsAsync<IOException>(() => service.InstallAsync(new("setup-test", 8192), root, root));
            check("Java 准备失败传回可重试错误", failed, null);
            check("Java 失败不覆盖原实例设置", File.ReadAllText(InstanceSettings.PathFor(root, "setup-test")) == before, null);
            java.Fail = false;
            using var canceled = new CancellationTokenSource();
            java.OnEnsure = canceled.Cancel;
            check("Java 阶段取消贯穿整条安装链路", await ThrowsAsync<OperationCanceledException>(() => service.InstallAsync(new("setup-test", 8192), root, root, ct: canceled.Token)), null);
            check("取消安装不覆盖原实例设置", File.ReadAllText(InstanceSettings.PathFor(root, "setup-test")) == before, null);
            java.OnEnsure = null;
            var calls = meta.Calls;
            check("已取消的请求不访问元数据", await ThrowsAsync<OperationCanceledException>(() => service.InstallAsync(new("setup-test", 2048), root, root, ct: canceled.Token)) && meta.Calls == calls, null);
            check("非法内存在任何 IO 前被拒绝", await ThrowsAsync<ArgumentOutOfRangeException>(() => service.InstallAsync(new("setup-test", 0), root, root)) && meta.Calls == calls, null);
            check("版本路径穿越在任何 IO 前被拒绝", await ThrowsAsync<ArgumentException>(() => service.InstallAsync(new("../escape", 2048), root, root)) && meta.Calls == calls, null);
            check("版本元数据错配被拒绝", await ThrowsAsync<InvalidDataException>(() => service.InstallAsync(new("wrong-id", 2048), root, root)), null);

            meta.OldVersion = true;
            await service.InstallAsync(new("setup-test", 2048), root, root);
            check("旧版没有 javaVersion 时使用 Java 8", java.Required == 8, null);
        }
        finally
        {
            // root 是本测试生成的临时目录。
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<bool> ThrowsAsync<T>(Func<Task> run) where T : Exception
    {
        try { await run(); return false; }
        catch (T) { return true; }
    }

    private sealed class ImmediateProgress(Action<FirstRunProgress> report) : IProgress<FirstRunProgress>
    {
        public void Report(FirstRunProgress value) => report(value);
    }

    private sealed class SetupMeta : IVersionMetaClient
    {
        public int Calls { get; private set; }
        public bool OldVersion { get; set; }
        public Task<VersionManifest> GetManifestAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<VersionJson> GetVersionAsync(string versionId, CancellationToken ct = default)
        {
            Calls++;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new VersionJson
            {
                Id = "setup-test", MainClass = "test.Main",
                JavaVersion = OldVersion ? null : new JavaVersionRef { MajorVersion = 21 },
                AssetIndex = new AssetIndexRef { Id = "test", Url = "https://example.invalid/index" },
            });
        }
        public Task<AssetIndex> GetAssetIndexAsync(AssetIndexRef reference, CancellationToken ct = default) => Task.FromResult(new AssetIndex
        {
            Objects = new() { ["test"] = new AssetObject { Hash = new string('a', 40), Size = 1 } },
        });
    }

    private sealed class SetupDownloader : IDownloader
    {
        public IReadOnlyList<DownloadEntry> Entries { get; private set; } = [];
        public Task<DownloadSummary> DownloadAsync(IReadOnlyList<DownloadEntry> entries, string rootDirectory,
            IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Entries = entries;
            progress?.Report(new(1, 1, entries.Count, entries.Count, "test-asset"));
            return Task.FromResult(new DownloadSummary(entries.Count, entries.Count, 0, 1, TimeSpan.Zero));
        }
    }

    private sealed class SetupJava : IJavaProvisioner
    {
        public int Required { get; private set; }
        public bool Fail { get; set; }
        public Action? OnEnsure { get; set; }
        public Task<JavaInstall> EnsureAsync(int majorVersion, string dataDirectory,
            IProgress<JavaProvisionProgress>? progress = null, CancellationToken ct = default)
        {
            Required = majorVersion;
            if (Fail) throw new IOException("模拟 Java 下载失败");
            OnEnsure?.Invoke();
            return Task.FromResult(new JavaInstall("java.exe", majorVersion, majorVersion.ToString(), "test", "x64"));
        }
    }
}
