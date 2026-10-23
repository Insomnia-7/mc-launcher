// M1 核心链路自检：纯逻辑（默认） + 可选联网下载（--live）。
// 用法：
//   Core.Checks.exe            离线逻辑检查
//   Core.Checks.exe --live     追加真实下载校验（用 .probe-cache 里的 1.21.4 版本 JSON）
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Launcher.Application.Abstractions;
using Launcher.Application.Auth;
using Launcher.Application.Diagnostics;
using Launcher.Application.Install;
using Launcher.Application.Instances;
using Launcher.Application.Launch;
using Launcher.Application.Mods;
using Launcher.Application.Update;
using Launcher.Domain.Content;
using Launcher.Domain.Install;
using Launcher.Domain.Java;
using Launcher.Domain.Launch;
using Launcher.Domain.Loaders;
using Launcher.Domain.Modpacks;
using Launcher.Domain.Versions;
using Launcher.Infrastructure.Http;
using Launcher.Infrastructure.Java;
using Launcher.Infrastructure.Meta;
using Launcher.Infrastructure.Modpacks;
using Launcher.Infrastructure.Security;

if (OptimizationChecks.IsJavaFixture(args)) return OptimizationChecks.RunJavaFixture();

var failures = new List<string>();
var passes = 0;

void Check(string name, bool condition, string? detail = null)
{
    if (condition) { passes++; Console.WriteLine($"  [ok]   {name}"); }
    else { failures.Add(detail is null ? name : $"{name} —— {detail}"); Console.WriteLine($"  [FAIL] {name}{(detail is null ? "" : " —— " + detail)}"); }
}

var ctx = new PlatformContext("windows", "x86_64", "10.0.19045") { HasCustomResolution = false };
var launcher = new LaunchContext(
    PlayerName: "Steve",
    Uuid: LaunchContext.OfflineUuid("Steve"),
    AccessToken: null,
    UserType: "legacy",
    LauncherName: "MCLauncher",
    LauncherVersion: "0.1.0",
    GameDirectory: @"C:\mc\instances\vanilla1214\.minecraft",
    AssetsRoot: @"C:\mc\assets",
    LibrariesRoot: @"C:\mc\libraries",
    ClientJarPath: @"C:\mc\versions\1.21.4\1.21.4.jar",
    MemoryMb: 4096,
    ExtraJvmArgs: null,
    ExtraGameArgs: null,
    WindowWidth: 1280,
    WindowHeight: 720,
    FullScreen: false);

Console.WriteLine("== 1. 规则过滤 ==");
{
    var winOnly = new Library
    {
        Name = "org.lwjgl:lwjgl:3.3.3",
        Rules = [new Rule { Action = "allow", Os = new OsRule { Name = "windows" } }],
        Downloads = new LibraryDownloads { Artifact = new DownloadArtifact { Path = "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar", Url = "https://example/win.jar", Size = 10, Sha1 = "x" } },
    };
    var osxOnly = new Library
    {
        Name = "org.lwjgl:lwjgl:3.3.3",
        Rules = [new Rule { Action = "allow", Os = new OsRule { Name = "osx" } }],
        Downloads = new LibraryDownloads { Artifact = new DownloadArtifact { Path = "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-osx.jar", Url = "https://example/osx.jar", Size = 10, Sha1 = "x" } },
    };

    var version = new VersionJson { Id = "test", MainClass = "net.minecraft.client.main.Main", Libraries = [winOnly, osxOnly] };
    var cp = ClassPathBuilder.Build(version, ctx, @"C:\mc\libraries", @"C:\mc\versions\1.21.4\1.21.4.jar");

    Check("windows 库被包含", cp.Entries.Any(p => p.EndsWith("lwjgl-3.3.3.jar", StringComparison.Ordinal)));
    Check("osx 库被排除", cp.Entries.All(p => !p.Contains("osx", StringComparison.Ordinal)));
    Check("client.jar 排最后", cp.Entries[^1].EndsWith("1.21.4.jar", StringComparison.Ordinal), cp.Entries[^1]);
}

Console.WriteLine("== 2. 占位符与启动参数（1.13+ arguments）==");
{
    var version = new VersionJson
    {
        Id = "1.21.4",
        Type = "release",
        MainClass = "net.minecraft.client.main.Main",
        Assets = "19",
        Libraries = [],
        Arguments = new Arguments
        {
            Jvm = ["-Djava.library.path=${natives_directory}", "-cp", "${classpath}"],
            Game = ["--username", "${auth_player_name}", "--uuid", "${auth_uuid}", "--accessToken", "${auth_access_token}",
                    "--version", "${version_name}", "--gameDir", "${game_directory}", "--assetsDir", "${assets_root}",
                    "--assetIndex", "${assets_index_name}", "--userType", "${user_type}"],
        },
    };

    var plan = LaunchPlanBuilder.Build(version, ctx, launcher, @"C:\java\bin\java.exe");

    Check("mainClass 正确", plan.MainClass == "net.minecraft.client.main.Main");
    Check("uuid 去掉横线", plan.GameArguments.Contains(launcher.Uuid.Replace("-", "")));
    Check("用户名注入", plan.GameArguments.Contains("Steve"));
    Check("离线无令牌时填 0", plan.GameArguments.Contains("0"));
    Check("version_name 注入", plan.GameArguments.Contains("1.21.4"));
    Check("assetIndex 注入", plan.GameArguments.Contains("19"));
    Check("-cp 被挪到末尾", plan.JvmArguments[^2] == "-cp" && plan.JvmArguments[^1].Contains("1.21.4.jar", StringComparison.Ordinal), string.Join(" | ", plan.JvmArguments.TakeLast(3)));
    Check("natives 路径注入", plan.JvmArguments.Any(a => a.StartsWith("-Djava.library.path=", StringComparison.Ordinal) && a.Contains("natives", StringComparison.Ordinal)));
    Check("内存参数存在", plan.JvmArguments.Contains("-Xmx4096M"));
    Check("没有残留 ${...}", !plan.JvmArguments.Concat(plan.GameArguments).Any(a => a.Contains("${", StringComparison.Ordinal)));
}

Console.WriteLine("== 3. 老版本 minecraftArguments ==");
{
    var version = new VersionJson
    {
        Id = "1.7.10",
        MainClass = "net.minecraft.client.main.Main",
        MinecraftArguments = "--username ${auth_player_name} --gameDir ${game_directory} --assetsDir ${game_assets}",
        Libraries = [],
    };

    var plan = LaunchPlanBuilder.Build(version, ctx, launcher, @"C:\java\bin\java.exe");

    Check("字符串参数被拆分并展开", plan.GameArguments.Contains("Steve") && plan.GameArguments.Contains("--gameDir"));
    Check("老版本补 -Djava.library.path", plan.JvmArguments.Any(a => a.StartsWith("-Djava.library.path=", StringComparison.Ordinal)));
    Check("老版本补 -cp", plan.JvmArguments.Contains("-cp"));
    Check("game_assets 展开为 virtual/legacy", plan.GameArguments.Any(a => a.Replace('\\', '/').EndsWith("assets/virtual/legacy", StringComparison.Ordinal)), string.Join(" ", plan.GameArguments.TakeLast(2)));
}

Console.WriteLine("== 4. 老式 natives 走解压而不是 classpath ==");
{
    var legacyNative = new Library
    {
        Name = "org.lwjgl.lwjgl:lwjgl-platform:2.9.4",
        Natives = new Dictionary<string, string> { ["windows"] = "natives-windows" },
        Downloads = new LibraryDownloads
        {
            Classifiers = new Dictionary<string, DownloadArtifact>
            {
                ["natives-windows"] = new DownloadArtifact { Path = "org/lwjgl/lwjgl-platform/2.9.4/lwjgl-platform-2.9.4-natives-windows.jar", Url = "https://example/nat.jar", Size = 5, Sha1 = "x" },
            },
        },
    };
    var version = new VersionJson { Id = "1.7.10", MainClass = "x.Y", Libraries = [legacyNative] };
    var cp = ClassPathBuilder.Build(version, ctx, @"C:\mc\libraries", @"C:\mc\versions\1.7.10\1.7.10.jar");

    Check("natives jar 不进 classpath", cp.Entries.All(p => !p.Contains("natives-windows", StringComparison.Ordinal)));
    Check("natives jar 被记录待解压", cp.NativeJars.Count == 1 && cp.NativeJars[0].Contains("natives-windows", StringComparison.Ordinal));
}

Console.WriteLine("== 5. argfile 转义 ==");
{
    var plan = new LaunchPlan(
        @"C:\java\bin\java.exe",
        "net.minecraft.client.main.Main",
        ["-Xmx2G", @"-Dfoo=C:\Users\me\mc"],
        ["--gameDir", @"C:\Users\me\我的 游戏"],
        @"C:\Users\me\我的 游戏");

    var content = plan.ToArgFileContent();

    Check("反斜杠转成正斜杠", content.Contains("C:/Users/me/mc") && !content.Contains(@"C:\Users"));
    Check("含空格参数加引号", content.Contains("\"C:/Users/me/我的 游戏\""));
    Check("mainClass 单独一行", content.Contains("net.minecraft.client.main.Main\n") || content.Contains("net.minecraft.client.main.Main\r\n"));
}

Console.WriteLine("== 6. 离线 UUID ==");
{
    var a = LaunchContext.OfflineUuid("Steve");
    var b = LaunchContext.OfflineUuid("Steve");
    Check("稳定可复现", a == b, a);
    Check("版本位为 3", a[14] == '3', a);
    Check("格式 8-4-4-4-12", a.Length == 36 && a[8] == '-' && a[13] == '-' && a[18] == '-' && a[23] == '-', a);
}

Console.WriteLine("== 7. 用户参数拆分 ==");
{
    var parts = ArgumentExpander.SplitArguments("-XX:+UseG1GC -Dname=\"my launcher\" -Xss1M");
    Check("引号内空格不被拆开", parts.Contains("my launcher") == false && parts.Any(p => p.Contains("my launcher")), string.Join(" | ", parts));
    Check("普通参数正常拆分", parts.Contains("-Xss1M") && parts.Contains("-XX:+UseG1GC"));
}

Console.WriteLine("== 8. Java 自动安装（Adoptium 响应 / sha256 / 解压 / 短路）==");
{
    // 每条 case 一个独立 dataDirectory，互不影响；fixture 全部内联，不依赖 scratch/ 和网络
    var scratchRoot = Path.Combine(FindRepoRoot() ?? Path.GetTempPath(), "scratch",
        "java-checks-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(scratchRoot);

    try
    {
        // 8a. 真实 21 响应：link / name / size / sha256 都要解析对；下到的字节 sha256 不符必须抛 IOException 并删掉坏包
        {
            var dataDir = Path.Combine(scratchRoot, "sha256");
            var handler = new StubHttpHandler(_ => AdoptiumReal21Json());
            using var http = new HttpClient(handler);
            var downloader = new FakeDownloader((_, _) => new byte[] { 1, 2, 3, 4 });
            var provisioner = new JavaProvisioner(http, downloader, new FakeJavaLocator());

            IOException? caught = null;
            try { await provisioner.EnsureAsync(21, dataDir); }
            catch (IOException ex) { caught = ex; }

            var request = downloader.Requests.FirstOrDefault();
            var cacheDir = Path.Combine(dataDir, "java", ".cache");
            var cachedFiles = Directory.Exists(cacheDir) ? Directory.GetFiles(cacheDir) : [];

            Check("sha256 不符抛 IOException", caught is not null, caught?.GetType().Name ?? "没抛");
            Check("错误信息带上了 Adoptium 给的 sha256",
                caught?.Message.Contains("d35f31e712f0fcf6ac5a093edc90204fbff22f720ba3950bd09d331d5e621636", StringComparison.Ordinal) == true,
                caught?.Message);
            Check("解析出安装包名", request?.RelativePath == "OpenJDK21U-jre_x64_windows_hotspot_21.0.12.1_1.zip", request?.RelativePath);
            Check("解析出 size", request?.Size == 48999141, request?.Size.ToString());
            Check("解析出 link",
                request?.Url.Contains("adoptium/temurin21-binaries", StringComparison.Ordinal) == true &&
                request.Url.EndsWith("OpenJDK21U-jre_x64_windows_hotspot_21.0.12.1_1.zip", StringComparison.Ordinal),
                request?.Url);
            Check("请求固定了厂商和平台",
                handler.Urls.Count == 1 &&
                handler.Urls[0].Contains("/v3/assets/latest/21/hotspot", StringComparison.Ordinal) &&
                handler.Urls[0].Contains("vendor=eclipse", StringComparison.Ordinal) &&
                handler.Urls[0].Contains("architecture=x64", StringComparison.Ordinal),
                handler.Urls.FirstOrDefault());
            Check("校验失败的包被删掉（不留脏缓存）", cachedFiles.Length == 0, string.Join(",", cachedFiles.Select(Path.GetFileName)));
        }

        // 8b. 解压：合成一个单顶层目录的 zip，装完必须是 java/21/bin/java.exe，且 staging 目录零残留
        {
            var dataDir = Path.Combine(scratchRoot, "extract");
            var zipBytes = BuildFakeJreZip("jdk-21.0.12.1+1-jre");
            var sha256 = Convert.ToHexStringLower(SHA256.HashData(zipBytes));
            var json = AdoptiumFixtureJson(
                "https://example.invalid/fake-jre.zip", sha256,
                "OpenJDK21U-jre_x64_windows_hotspot_fake.zip", zipBytes.Length, 21, "21.0.12.1+1-test");

            using var http = new HttpClient(new StubHttpHandler(_ => json));
            var downloader = new FakeDownloader((_, _) => zipBytes);
            // 探针返回 null：合成出来的 java.exe 当然跑不起来，正好验证「用 API 的版本号兜底」
            var provisioner = new JavaProvisioner(http, downloader, new FakeJavaLocator(probe: _ => null));

            var install = await provisioner.EnsureAsync(21, dataDir);

            var javaDir = Path.Combine(dataDir, "java");
            var installDir = Path.Combine(javaDir, "21");
            var stagingLeft = Directory.Exists(javaDir)
                ? Directory.GetDirectories(javaDir).Where(d => Path.GetFileName(d).StartsWith("21.staging-", StringComparison.Ordinal)).ToArray()
                : [];
            var cacheLeft = Directory.Exists(Path.Combine(javaDir, ".cache"))
                ? Directory.GetFiles(Path.Combine(javaDir, ".cache"))
                : [];

            Check("装到 java/21/bin/java.exe",
                install.ExecutablePath == Path.Combine(installDir, "bin", "java.exe") && File.Exists(install.ExecutablePath),
                install.ExecutablePath);
            Check("zip 的顶层目录被剥掉（bin 直接挂在安装目录下）", File.Exists(Path.Combine(installDir, "bin", "java.exe")));
            Check("staging 目录零残留", stagingLeft.Length == 0, string.Join(",", stagingLeft.Select(Path.GetFileName)));
            Check("安装包用完即删", cacheLeft.Length == 0, string.Join(",", cacheLeft.Select(Path.GetFileName)));
            Check("探不出 -version 时用 API 的版本信息兜底",
                install.MajorVersion == 21 && install.Vendor == "Adoptium" && install.Arch == "x64" &&
                install.VersionString == "21.0.12.1+1-test",
                install.ToString());
        }

        // 8c. 短路：本地已经有 java/21 就直接用，既不联网也不下载
        {
            var dataDir = Path.Combine(scratchRoot, "existing");
            var managedExe = Path.Combine(dataDir, "java", "21", "bin", "java.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(managedExe)!);
            await File.WriteAllTextAsync(managedExe, "fake java");

            var expected = new JavaInstall(managedExe, 21, "21.0.12.1", "Adoptium", "x64");
            var handler = new StubHttpHandler(_ => AdoptiumReal21Json());
            using var http = new HttpClient(handler);
            var downloader = new FakeDownloader((_, _) => new byte[] { 1 });
            var provisioner = new JavaProvisioner(http, downloader,
                new FakeJavaLocator(probe: path => path == managedExe ? expected : null));

            var install = await provisioner.EnsureAsync(21, dataDir);

            Check("已装则直接复用本地那份", install == expected, install.ToString());
            Check("已装则零下载", downloader.Calls == 0, $"下载器被调了 {downloader.Calls} 次");
            Check("已装则零联网", handler.Calls == 0, $"Adoptium 被请求了 {handler.Calls} 次");
        }

        // 8d. 明显不合法的入参要在联网之前就挡掉
        {
            using var http = new HttpClient(new StubHttpHandler(_ => AdoptiumReal21Json()));
            var provisioner = new JavaProvisioner(http, new FakeDownloader((_, _) => null), new FakeJavaLocator());

            ArgumentOutOfRangeException? caught = null;
            try { await provisioner.EnsureAsync(0, Path.Combine(scratchRoot, "invalid")); }
            catch (ArgumentOutOfRangeException ex) { caught = ex; }

            Check("主版本号为 0 直接拒绝", caught is not null, caught?.GetType().Name ?? "没抛");
        }
    }
    finally
    {
        try { Directory.Delete(scratchRoot, recursive: true); } catch { /* 清理失败不影响结论 */ }
    }
}

Console.WriteLine("== 9. 离线身份（对齐 Java UUID.nameUUIDFromBytes）==");
{
    // 期望值是本机 java 真跑 UUID.nameUUIDFromBytes(("OfflinePlayer:"+name).getBytes(UTF_8)) 得到的，
    // 所以这一组同时钉住了「MD5 前 16 字节」「大端/小端字节序」「版本位 3」「变体位 RFC4122」四件事。
    (string Name, string Uuid)[] golden =
    [
        ("Notch", "b50ad385-829d-3141-a216-7e7d7539ba7f"),
        ("jeb_", "a762f560-4fce-3236-812a-b80efff0b62b"),
        ("Steve", "5627dd98-e6be-3c21-b8a8-e92344183641"),
        ("Developer", "a01a21f5-c117-3c1f-b5d0-e055b927f733"),
    ];

    foreach (var (name, expected) in golden)
    {
        var id = OfflineIdentityFactory.Create(name);
        Check($"离线 UUID {name} 与 Java 一致", id.Uuid == expected, $"{id.Uuid} != {expected}");
        Check($"离线 UUID {name} 与启动参数用的算法同源", id.Uuid == LaunchContext.OfflineUuid(name), id.Uuid);
        Check($"离线 UUID {name} 版本位=3、变体位=RFC4122",
            id.Uuid[14] == '3' && "89ab".Contains(id.Uuid[19], StringComparison.Ordinal), id.Uuid);
    }

    Check("默认离线名是 Developer",
        OfflineIdentityFactory.Create(OfflineIdentityFactory.DefaultName).Name == "Developer");
    Check("名字 2 字符不合法", !OfflineIdentityFactory.IsValidName("ab"));
    Check("名字 3 字符合法", OfflineIdentityFactory.IsValidName("abc"));
    Check("名字 16 字符合法", OfflineIdentityFactory.IsValidName(new string('a', 16)));
    Check("名字 17 字符不合法", !OfflineIdentityFactory.IsValidName(new string('a', 17)));
    Check("中文名不合法", !OfflineIdentityFactory.IsValidName("玩家一号"));
    Check("带空格不合法", !OfflineIdentityFactory.IsValidName("a b"));
    Check("null 不合法", !OfflineIdentityFactory.IsValidName(null));
    Check("下划线数字混合合法", OfflineIdentityFactory.IsValidName("a_b_C9"));
    Check("大小写不同 → UUID 不同",
        OfflineIdentityFactory.Create("Steve").Uuid != OfflineIdentityFactory.Create("steve").Uuid);

    var badNameThrew = false;
    try { OfflineIdentityFactory.Create("bad name"); }
    catch (ArgumentException) { badNameThrew = true; }
    Check("非法名字 Create 抛 ArgumentException", badNameThrew);
}

Console.WriteLine("== 10. 账号簿：增删选 + 往返序列化 ==");
{
    var dir = NewTempDataDir("accounts");
    try
    {
        var lib = AccountLibrary.Load(dir);
        Check("空目录 → 0 个账号", lib.Accounts.Count == 0);
        Check("空目录 → 没有选中账号", lib.Selected is null);
        Check("空目录 → 离线身份是 Developer",
            lib.OfflineIdentity.Name == "Developer" && lib.OfflineIdentity.Uuid == "a01a21f5-c117-3c1f-b5d0-e055b927f733",
            lib.OfflineIdentity.Uuid);

        var alpha = SampleAccount("11111111111111111111111111111111", "Alpha");
        var beta = SampleAccount("22222222222222222222222222222222", "Beta");

        lib.AddOrUpdate(alpha);
        Check("加第一个账号后自动选中", lib.Selected?.Uuid == alpha.Uuid);
        lib.AddOrUpdate(beta);
        Check("加第二个账号 → 2 个", lib.Accounts.Count == 2);
        var snapshot = lib.Copy();
        Check("账号副本保留当前选择", snapshot.Selected?.Uuid == lib.Selected?.Uuid);
        snapshot.Select(alpha.Uuid);
        Check("副本切换不改变原账号簿选择", lib.Selected?.Uuid == beta.Uuid);
        snapshot.Remove(beta.Uuid);
        Check("副本删除不改变原账号簿内容", lib.Accounts.Count == 2 && snapshot.Accounts.Count == 1);
        snapshot.SetOfflineName("Other_Name");
        Check("副本编辑离线身份不影响原账号簿", lib.OfflineIdentity.Name != "Other_Name");

        Check("后加的成为选中项", lib.Selected?.Uuid == beta.Uuid);

        lib.AddOrUpdate(alpha with { Name = "Alpha2" });
        Check("同 uuid 覆盖不新增", lib.Accounts.Count == 2);
        Check("同 uuid 覆盖后名字更新", lib.Accounts.First(a => a.Uuid == alpha.Uuid).Name == "Alpha2");

        lib.Select(alpha.Uuid);
        Check("Select 切换生效", lib.Selected?.Uuid == alpha.Uuid);
        lib.Select(null);
        Check("Select(null) 回到离线身份", lib.Selected is null);
        lib.Select("ffffffffffffffffffffffffffffffff");
        Check("Select 未知 uuid 不改变选中", lib.Selected is null);

        lib.SetOfflineName("Notch");
        Check("改离线名后 UUID 跟着重算",
            lib.OfflineIdentity.Uuid == "b50ad385-829d-3141-a216-7e7d7539ba7f", lib.OfflineIdentity.Uuid);

        lib.Select(beta.Uuid);
        lib.Save(dir);

        Check("落盘到 accounts.json", File.Exists(AccountLibrary.PathFor(dir)));
        var json = File.ReadAllText(AccountLibrary.PathFor(dir));
        Check("落盘含 selected/offlineName/accounts",
            json.Contains("\"selected\"") && json.Contains("\"offlineName\"") && json.Contains("\"accounts\""));
        Check("落盘不含计算属性", !json.Contains("IsExpired") && !json.Contains("NeedsRefresh"));

        var reloaded = AccountLibrary.Load(dir);
        Check("往返后账号数不变", reloaded.Accounts.Count == 2);
        Check("往返后选中项不变", reloaded.Selected?.Uuid == beta.Uuid);
        Check("往返后离线身份不变",
            reloaded.OfflineIdentity.Name == "Notch" &&
            reloaded.OfflineIdentity.Uuid == "b50ad385-829d-3141-a216-7e7d7539ba7f");
        Check("往返后 refresh_token 未损坏",
            reloaded.Accounts.First(a => a.Uuid == beta.Uuid).RefreshToken == beta.RefreshToken);
        Check("往返后过期时刻未损坏",
            reloaded.Accounts.First(a => a.Uuid == beta.Uuid).ExpiresAt == beta.ExpiresAt);

        Check("删除存在的账号返回 true", reloaded.Remove(beta.Uuid));
        Check("删掉当前账号后退回离线身份", reloaded.Selected is null);
        Check("删除不存在的账号返回 false", !reloaded.Remove("ffffffffffffffffffffffffffffffff"));

        reloaded.Save(dir);
        var afterRemove = AccountLibrary.Load(dir);
        Check("删除后往返只剩 1 个且无选中",
            afterRemove.Accounts.Count == 1 && afterRemove.Selected is null);
    }
    finally { TryDelete(dir); }
}

Console.WriteLine("== 11. 旧 account.json 迁移 ==");
{
    var dir = NewTempDataDir("migrate");
    try
    {
        var legacy = SampleAccount("33333333333333333333333333333333", "Gamma");
        var legacyPath = Path.Combine(dir, AccountStore.FileName);
        File.WriteAllText(legacyPath, JsonSerializer.Serialize(legacy, new JsonSerializerOptions { WriteIndented = true }));

        var migrated = AccountLibrary.Load(dir);
        Check("旧存档迁移成 1 个账号",
            migrated.Accounts.Count == 1 && migrated.Accounts[0].Uuid == legacy.Uuid);
        Check("迁移后旧账号自动成为选中项", migrated.Selected?.Uuid == legacy.Uuid);
        Check("迁移后生成 accounts.json", File.Exists(AccountLibrary.PathFor(dir)));
        Check("迁移不删旧文件", File.Exists(legacyPath));

        // 旧文件被改写也不该再被读：否则每次启动都会盖掉用户后来的选择
        File.WriteAllText(legacyPath, JsonSerializer.Serialize(SampleAccount("44444444444444444444444444444444", "Delta")));
        var second = AccountLibrary.Load(dir);
        Check("accounts.json 优先，不重复迁移",
            second.Accounts.Count == 1 && second.Accounts[0].Uuid == legacy.Uuid);

        Check("兼容壳 AccountStore.Load 返回选中账号", AccountStore.Load(dir)?.Uuid == legacy.Uuid);

        AccountStore.Clear(dir);
        var afterClear = AccountLibrary.Load(dir);
        Check("退出登录后没有选中账号", afterClear.Selected is null);
        Check("退出登录摘掉当前账号", afterClear.Accounts.Count == 0);
        Check("退出登录删掉旧 account.json", !File.Exists(legacyPath));
        Check("退出登录保留 accounts.json", File.Exists(AccountLibrary.PathFor(dir)));

        File.WriteAllText(AccountLibrary.PathFor(dir), "{ 这不是 JSON");
        var broken = AccountLibrary.Load(dir);
        Check("accounts.json 写坏 → 空库且不抛",
            broken.Accounts.Count == 0 && broken.Selected is null && broken.OfflineIdentity.Name == "Developer");

        File.WriteAllText(AccountLibrary.PathFor(dir), "{\"offlineName\":\"坏名字!\",\"accounts\":[]}");
        Check("非法离线名回落 Developer", AccountLibrary.Load(dir).OfflineIdentity.Name == "Developer");
    }
    finally { TryDelete(dir); }
}

Console.WriteLine("== 12. ClassPathBuilder：Forge / NeoForge patched client ==");
{
    // patched client 不在版本 JSON 的 libraries 里（installer 只把它落到 libraries 目录下），
    // 所以这一组必须用真实临时目录 + 真实文件：光看 version JSON 验不出「磁盘上有没有」。
    const string NeoBootstrapMainClass = "cpw.mods.bootstraplauncher.BootstrapLauncher";
    var root = NewTempTree("neoforge");
    try
    {
        var librariesRoot = Path.Combine(root, "libraries");
        var vanillaClient = Path.Combine(root, "versions", "1.21.4", "1.21.4.jar");

        // 按 maven 相对路径在 libraries 下放一个内容随意的 jar，返回它的绝对路径
        string PutJar(string relativePath)
        {
            var full = Path.Combine(librariesRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "patched client");
            return full;
        }

        static bool Has(ClassPathResult cp, string path) => cp.Entries.Contains(path, StringComparer.OrdinalIgnoreCase);

        // 12a. NeoForge：版本 id neoforge-21.4.123 **一个 client jar 都不要** ——
        //   · 塞 patched client：它和 universal jar 的自动模块名都是 neoforge
        //     → "reads more than one module named neoforge"；
        //   · 塞原版 client.jar：自动模块 _1._21._4 与 FML 建的 minecraft 包裹名重叠
        //     → "Module minecraft contains package net.minecraft.data, module _1._21._4 exports ..."。
        // 游戏本体由 FML 按 --fml.neoFormVersion 从 libraries 里取。
        var neoforgeClient = PutJar("net/neoforged/neoforge/21.4.123/neoforge-21.4.123-client.jar");
        var neoVersion = new VersionJson { Id = "neoforge-21.4.123", MainClass = NeoBootstrapMainClass, Libraries = [] };
        var neoCp = ClassPathBuilder.Build(neoVersion, ctx, librariesRoot, vanillaClient);

        Check("NeoForge：不塞 client jar（模块名冲突）", !Has(neoCp, neoforgeClient),
            string.Join(" | ", neoCp.Entries));
        Check("NeoForge：也不塞原版 client.jar（包裹名重叠）", !Has(neoCp, vanillaClient),
            string.Join(" | ", neoCp.Entries));

        // 12b. 同一个磁盘布局、主类换成原版：只有 bootstrap 主类才代表「这是 patched 客户端」，不能误顶替
        // 注意用 Forge 的 id：NeoForge id（neoforge-*）是另一条「一个 client jar 都不要」的分支，见 12a。
        var vanillaVersion = new VersionJson
        {
            Id = "1.20.1-forge-47.4.18",
            MainClass = "net.minecraft.client.main.Main",
            Libraries = [],
        };
        var vanillaCp = ClassPathBuilder.Build(vanillaVersion, ctx, librariesRoot, vanillaClient);

        Check("非 bootstrap 主类：保留原版 client.jar",
            vanillaCp.Entries.Count > 0 && string.Equals(vanillaCp.Entries[^1], vanillaClient, StringComparison.OrdinalIgnoreCase),
            vanillaCp.Entries.Count == 0 ? "(空)" : vanillaCp.Entries[^1]);
        Check("非 bootstrap 主类：不把磁盘上的 patched client 塞进来", !Has(vanillaCp, neoforgeClient),
            string.Join(" | ", vanillaCp.Entries));

        // 12c. Forge 常规布局：1.20.1-forge-47.4.18 → net/minecraftforge/forge/1.20.1-47.4.18/
        var forgeClient = PutJar("net/minecraftforge/forge/1.20.1-47.4.18/forge-1.20.1-47.4.18-client.jar");
        var forgeVersion = new VersionJson { Id = "1.20.1-forge-47.4.18", MainClass = NeoBootstrapMainClass, Libraries = [] };
        var forgeCp = ClassPathBuilder.Build(forgeVersion, ctx, librariesRoot, vanillaClient);

        Check("Forge：命中 net/minecraftforge/forge/1.20.1-47.4.18/…-client.jar",
            Has(forgeCp, forgeClient) && !Has(forgeCp, vanillaClient), string.Join(" | ", forgeCp.Entries));

        // 12d. NeoForge 1.20.1 的等价布局：版本 id 仍是 1.20.1-forge-47.1.1，jar 却落在 net/neoforged/forge 下
        var neoForgeClient = PutJar("net/neoforged/forge/1.20.1-47.1.1/forge-1.20.1-47.1.1-client.jar");
        var neoForgeVersion = new VersionJson { Id = "1.20.1-forge-47.1.1", MainClass = NeoBootstrapMainClass, Libraries = [] };
        var neoForgeCp = ClassPathBuilder.Build(neoForgeVersion, ctx, librariesRoot, vanillaClient);

        Check("NeoForge 1.20.1：命中第二个候选路径（net/neoforged/forge/…）",
            Has(neoForgeCp, neoForgeClient) && !Has(neoForgeCp, vanillaClient), string.Join(" | ", neoForgeCp.Entries));

        // 12e. bootstrap 主类但磁盘上两个候选都没有 → 静默回退原版 client.jar，不许抛
        // （用 Forge id：NeoForge id 走「一个 client jar 都不要」的分支，不适用于本用例）
        var missingVersion = new VersionJson { Id = "1.20.1-forge-99.9.9", MainClass = NeoBootstrapMainClass, Libraries = [] };
        Exception? resolveFailure = null;
        ClassPathResult? missingCp = null;
        try { missingCp = ClassPathBuilder.Build(missingVersion, ctx, librariesRoot, vanillaClient); }
        catch (Exception ex) { resolveFailure = ex; }

        Check("候选都不存在：回退原版 client.jar 且不抛",
            resolveFailure is null && missingCp is { Entries.Count: > 0 } &&
            string.Equals(missingCp.Entries[^1], vanillaClient, StringComparison.OrdinalIgnoreCase),
            resolveFailure is not null ? resolveFailure.GetType().Name + ": " + resolveFailure.Message
                : missingCp?.Entries.Count.ToString() ?? "(null)");
    }
    finally { TryDelete(root); }
}

Console.WriteLine("== 13. LaunchPlanBuilder：编码参数与 JVM 档位插入位置 ==");
{
    // 只放最小可用的 jvm 段：java.library.path 存在，兜底插的那条就不会挤到 index 0，位置断言才稳
    var version = new VersionJson
    {
        Id = "1.21.4",
        Type = "release",
        MainClass = "net.minecraft.client.main.Main",
        Libraries = [],
        Arguments = new Arguments
        {
            Jvm = ["-Djava.library.path=${natives_directory}", "-cp", "${classpath}"],
            Game = ["--username", "${auth_player_name}", "--version", "${version_name}"],
        },
    };

    var defaultPlan = LaunchPlanBuilder.Build(version, ctx, launcher, @"C:\java\bin\java.exe");
    Check("默认补 -Dfile.encoding=UTF-8",
        defaultPlan.JvmArguments.Count(a => a == "-Dfile.encoding=UTF-8") == 1,
        string.Join(" | ", defaultPlan.JvmArguments));
    Check("默认补 -Dstdout.encoding=UTF-8", defaultPlan.JvmArguments.Contains("-Dstdout.encoding=UTF-8"));
    Check("默认补 -Dstderr.encoding=UTF-8", defaultPlan.JvmArguments.Contains("-Dstderr.encoding=UTF-8"));

    // 版本 JSON 自己声明了 GBK：只留它那一条，不许再追加 UTF-8（后写的会覆盖前写的，用户等于白配）
    var declaredVersion = new VersionJson
    {
        Id = "1.21.4",
        Type = "release",
        MainClass = "net.minecraft.client.main.Main",
        Libraries = [],
        Arguments = new Arguments
        {
            Jvm = ["-Djava.library.path=${natives_directory}", "-Dfile.encoding=GBK", "-cp", "${classpath}"],
            Game = ["--username", "${auth_player_name}"],
        },
    };
    var declaredPlan = LaunchPlanBuilder.Build(declaredVersion, ctx, launcher, @"C:\java\bin\java.exe");
    var declaredEncoding = declaredPlan.JvmArguments
        .Where(a => a.StartsWith("-Dfile.encoding=", StringComparison.Ordinal)).ToList();

    Check("已声明 GBK 时只剩一条 -Dfile.encoding= 且是版本自己的",
        declaredEncoding.Count == 1 && declaredEncoding[0] == "-Dfile.encoding=GBK",
        string.Join(" | ", declaredEncoding));

    // Fast 档：预设开关插在 -Xms 之后，绝不挤走内存开关
    var fastPlan = LaunchPlanBuilder.Build(version, ctx, launcher with { JvmPreset = JvmPreset.Fast },
        @"C:\java\bin\java.exe");
    var fastJvm = fastPlan.JvmArguments;
    Check("Fast：前两个参数仍是 -Xmx / -Xms",
        fastJvm.Count > 1 &&
        fastJvm[0].StartsWith("-Xmx", StringComparison.Ordinal) &&
        fastJvm[1].StartsWith("-Xms", StringComparison.Ordinal),
        string.Join(" | ", fastJvm.Take(3)));
    Check("Fast：预设参数从第 3 个开始",
        fastJvm.Count > 2 && fastJvm[2] == "-XX:+UnlockExperimentalVMOptions" &&
        fastJvm.Skip(2).Contains("-XX:+UseG1GC"),
        string.Join(" | ", fastJvm.Take(4)));
    Check("Fast：含 -XX:+UnlockExperimentalVMOptions",
        fastJvm.Contains("-XX:+UnlockExperimentalVMOptions"));
}

Console.WriteLine("== 14. StartupDiagnostics：崩溃日志翻译 ==");
{
    var dir = NewTempTree("diagnostics");
    try
    {
        string WriteLog(string name, string content)
        {
            var path = Path.Combine(dir, name);
            File.WriteAllText(path, content);
            return path;
        }

        // 14a. 模块冲突：a=minecraft 说明对方是改写原版类的 mod，Summary 必须点出两边名字
        var moduleLog = WriteLog("module-conflict.log", """
            [12:00:01] [main/INFO]: Launching Minecraft 1.20.1 with 2 mods
            java.lang.module.ResolutionException: Modules minecraft and customskinloader export package net.minecraft.client.renderer to module xaerominimap
            Error occurred during initialization of boot layer
            """);
        var moduleDiag = StartupDiagnostics.Analyze(moduleLog);
        Check("模块冲突：Summary 同时提到 customskinloader 与 minecraft",
            moduleDiag is not null &&
            moduleDiag.Summary.Contains("customskinloader", StringComparison.Ordinal) &&
            moduleDiag.Summary.Contains("minecraft", StringComparison.Ordinal),
            moduleDiag?.Summary ?? "(null)");

        // 14b. 堆内存不足
        var oomDiag = StartupDiagnostics.Analyze(WriteLog("oom.log", """
            [12:00:02] [main/WARN]: Loading 380 mods
            java.lang.OutOfMemoryError: Java heap space
                at net.minecraft.client.main.Main.main(Main.java:245)
            """));
        Check("内存不足：Summary 提到内存", oomDiag?.Summary.Contains("内存", StringComparison.Ordinal) == true,
            oomDiag?.Summary ?? "(null)");

        // 14c. bootstrap 主类没进 classpath（正是本文件第 12 组在防的那种情况）
        var mainClassDiag = StartupDiagnostics.Analyze(WriteLog("mainclass.log", """
            [12:00:03] [main/INFO]: Using bootstraplauncher
            Error: Could not find or load main class cpw.mods.bootstraplauncher.BootstrapLauncher
            """));
        Check("主类缺失：Summary 提到主类", mainClassDiag?.Summary.Contains("主类", StringComparison.Ordinal) == true,
            mainClassDiag?.Summary ?? "(null)");

        // 14d. 一切正常 → 不给假警报，返回 null 让调用方退回「看日志」兜底
        var healthyDiag = StartupDiagnostics.Analyze(WriteLog("healthy.log", """
            [12:00:04] [main/INFO]: Setting user: Steve
            [12:00:05] [main/INFO]: Backend library: LWJGL version 3.3.3+5
            [12:00:06] [Render thread/INFO]: OpenGL debug message: id=0, source=API
            [12:00:07] [main/INFO]: Started serving on 25565
            """));
        Check("正常日志：返回 null", healthyDiag is null, healthyDiag?.Summary ?? "(null)");

        // 14e. 日志文件本身不存在 → null，不抛
        Check("日志不存在：返回 null 不抛", StartupDiagnostics.Analyze(Path.Combine(dir, "not-there.log")) is null);
    }
    finally { TryDelete(dir); }
}

Console.WriteLine("== 15. 更新清单解析与版本比较 ==");
{
    const string goodJson = """
        {
          "version": "0.2.0",
          "notes": "修了几个崩溃",
          "files": [
            { "path": "MCLauncher.dll", "url": "files/MCLauncher.dll", "sha256": "ABCDEF09", "size": 12 },
            { "path": "sub/extra.txt", "url": "https://example.test/extra.txt", "size": 3 }
          ]
        }
        """;

    var manifest = UpdateManifestParser.Parse(goodJson);
    Check("清单：版本号解析", manifest.Version == "0.2.0", manifest.Version);
    Check("清单：文件数量", manifest.Files.Count == 2, manifest.Files.Count.ToString());
    Check("清单：sha256 统一小写", manifest.Files[0].Sha256 == "abcdef09", manifest.Files[0].Sha256);
    Check("清单：总字节", manifest.TotalBytes == 15, manifest.TotalBytes.ToString());
    Check("清单：entry 缺省回退", manifest.EntryOrDefault("MCLauncher.exe") == "MCLauncher.exe",
        manifest.EntryOrDefault("MCLauncher.exe"));
    Check("清单：路径保持 / 分隔", manifest.Files[1].Path == "sub/extra.txt", manifest.Files[1].Path);

    bool Rejects(string json)
    {
        try
        {
            UpdateManifestParser.Parse(json);
            return false;
        }
        catch (UpdateManifestException)
        {
            return true;
        }
    }

    Check("清单：缺 version 要拒绝",
        Rejects("""{ "files": [ { "path": "a", "url": "b" } ] }"""));
    Check("清单：缺 url 要拒绝",
        Rejects("""{ "version": "1.0.0", "files": [ { "path": "a" } ] }"""));
    Check("清单：路径跳出目录要拒绝",
        Rejects("""{ "version": "1.0.0", "files": [ { "path": "../evil.dll", "url": "b" } ] }"""));
    Check("清单：空文件列表要拒绝",
        Rejects("""{ "version": "1.0.0", "files": [] }"""));
    Check("清单：坏 JSON 要拒绝",
        Rejects("{ not json"));

    Check("版本：0.2.0 比 0.1.0 新", UpdateVersion.IsNewer("0.2.0", "0.1.0"));
    Check("版本：相同不算新", !UpdateVersion.IsNewer("0.2.0", "0.2.0"));
    Check("版本：正式版比预发布新", UpdateVersion.IsNewer("1.0.0", "1.0.0-beta"));
    Check("版本：预发布版本低于正式版", !UpdateVersion.IsNewer("1.0.0-beta", "1.0.0"));
    Check("版本：v 前缀可用", UpdateVersion.IsNewer("v0.3.0", "0.2.9"));
    Check("版本：当前为空视为有新版本", UpdateVersion.IsNewer("0.0.1", null));
    Check("版本：更旧的清单不算更新", !UpdateVersion.IsNewer("0.1.0", "0.2.0"));
    Check("版本：多段数字逐段比较", UpdateVersion.IsNewer("1.10.0", "1.9.9"));
}

// == 16. 账号簿加密（ISecretProtector）
{
    Console.WriteLine("== 16. 账号簿加密（ISecretProtector）==");
    var dir = NewTempDataDir("secret");
    try
    {
        var good = new FakeProtector(0x5A);
        var lib = AccountLibrary.Load(dir, good);
        lib.AddOrUpdate(SampleAccount("11111111-1111-3111-8111-111111111111", "Alice"));
        lib.Save(dir, good);

        var bytes = File.ReadAllBytes(AccountLibrary.PathFor(dir));
        Check("加密存档带 MCLA1 魔数", bytes.Length > 5 && bytes[0] == (byte)'M' && bytes[1] == (byte)'C' && bytes[2] == (byte)'L' && bytes[3] == (byte)'A' && bytes[4] == (byte)'1');
        var raw = Encoding.UTF8.GetString(bytes);
        Check("加密存档不含明文 token", !raw.Contains("mc-access-Alice", StringComparison.Ordinal) && !raw.Contains("ms-refresh-Alice", StringComparison.Ordinal));

        var reopened = AccountLibrary.Load(dir, good);
        Check("同密钥能解出账号", reopened.Accounts.Count == 1 && reopened.Selected?.Name == "Alice");

        var wrong = AccountLibrary.Load(dir, new FakeProtector(0x33));
        Check("换密钥解不开就是空库", wrong.Accounts.Count == 0 && wrong.Selected is null);

        var none = AccountLibrary.Load(dir);
        Check("不给 protector 读加密档也不抛", none.Accounts.Count == 0 && none.Selected is null);

        var dir2 = NewTempDataDir("secret-plain");
        try
        {
            var plain = AccountLibrary.Load(dir2);
            plain.AddOrUpdate(SampleAccount("22222222-2222-3222-8222-222222222222", "Bob"));
            plain.Save(dir2);
            Check("不给 protector 落盘是明文 JSON", Encoding.UTF8.GetString(File.ReadAllBytes(AccountLibrary.PathFor(dir2))).TrimStart().StartsWith('{'));

            var migrated = AccountLibrary.Load(dir2, good);
            Check("加密读取器能读明文旧档", migrated.Accounts.Count == 1 && migrated.Selected?.Name == "Bob");
            migrated.Save(dir2, good);
            Check("迁移后写成密文", File.ReadAllBytes(AccountLibrary.PathFor(dir2))[0] == (byte)'M');
            Check("迁移后仍可读回", AccountLibrary.Load(dir2, good).Selected?.Name == "Bob");
        }
        finally { TryDelete(dir2); }

        if (OperatingSystem.IsWindows() && DpapiSecretProtector.TryCreate() is { } dpapi)
        {
            var cipher = dpapi.Protect("{\"token\":\"abc\"}");
            Check("DPAPI 密文不含明文", !Encoding.UTF8.GetString(cipher).Contains("abc", StringComparison.Ordinal));
            Check("DPAPI 能解回原文", dpapi.TryUnprotect(cipher) == "{\"token\":\"abc\"}");
            Check("DPAPI 遇到垃圾字节返回 null", dpapi.TryUnprotect([1, 2, 3, 4]) is null);
        }
        else
        {
            Console.WriteLine("        （跳过 DPAPI：当前平台不可用）");
        }
    }
    finally { TryDelete(dir); }
}
if (args.Contains("--live"))
{
    Console.WriteLine("== 17. 真实下载（1.21.4 client.jar）==");
    var repoRoot = FindRepoRoot();
    var cache = repoRoot is null ? null : Path.Combine(repoRoot, ".probe-cache", "version-1.21.4.json");
    if (cache is null || !File.Exists(cache))
    {
        failures.Add("找不到 .probe-cache/version-1.21.4.json，无法做联网校验");
    }
    else
    {
        var json = await File.ReadAllTextAsync(cache);
        var version = JsonSerializer.Deserialize<VersionJson>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var client = version.Downloads?.Client!;

        // 注意：本机沙箱对子进程写 %TEMP% 有限制，落到仓库内临时目录更稳
        var target = Path.Combine(repoRoot!, "scratch", "dl-check-" + Guid.NewGuid().ToString("N")[..8]);
        var entry = new DownloadEntry(client.Url, "versions/1.21.4/1.21.4.jar", client.Size, client.Sha1, DownloadKind.Client);
        using var http = MojangMetaClient.CreateHttpClient("MCLauncher", "0.1.0");
        var downloader = new HttpDownloader(http, new DownloadOptions(MaxConcurrency: 4));

        var sw = Stopwatch.StartNew();
        try
        {
            var summary = await downloader.DownloadAsync([entry], target);
            sw.Stop();

            var file = new FileInfo(Path.Combine(target, entry.RelativePath));
            Check("client.jar 落地且大小正确", file.Exists && file.Length == client.Size, $"{file.Length} vs {client.Size}");
            Check("下载摘要包含 1 个文件", summary.TotalFiles == 1);
            Console.WriteLine($"        耗时 {sw.Elapsed.TotalSeconds:F1}s，{summary.DownloadedBytes / 1024.0 / 1024.0:F1} MB，{summary.BytesPerSecond / 1024.0 / 1024.0:F1} MB/s");

            // 第二次必须命中"已存在且 sha1 匹配"的快路径
            var second = await downloader.DownloadAsync([entry], target);
            Check("重复下载走跳过路径", second.SkippedFiles == 1 && second.DownloadedFiles == 0, $"skipped={second.SkippedFiles} downloaded={second.DownloadedFiles}");
        }
        catch (Exception ex)
        {
            var detail = ex is AggregateException agg
                ? string.Join(" || ", agg.Flatten().InnerExceptions.Select(e => $"{e.GetType().Name}: {e.Message}"))
                : $"{ex.GetType().Name}: {ex.Message}";
            failures.Add("联网下载失败：" + detail);
            Console.Error.WriteLine(ex);
        }
        finally
        {
            try { Directory.Delete(target, recursive: true); } catch { /* 清理失败不影响结论 */ }
        }
    }
}

Console.WriteLine("== 18. 编码参数（-D 属性判重）==");
{
    var version = new VersionJson
    {
        Id = "1.21.4",
        MainClass = "net.minecraft.client.main.Main",
        Libraries = [],
        Arguments = new Arguments
        {
            Jvm = ["-Djava.library.path=${natives_directory}", "-cp", "${classpath}"],
            Game = ["--username", "${auth_player_name}"],
        },
    };

    var plan = LaunchPlanBuilder.Build(version, ctx, launcher, @"C:\java\bin\java.exe");
    string[] encodingKeys = ["file.encoding", "stdout.encoding", "stderr.encoding", "sun.stdout.encoding", "sun.stderr.encoding"];

    Check("默认参数含 -Dfile.encoding=UTF-8", plan.JvmArguments.Contains("-Dfile.encoding=UTF-8"));
    foreach (var key in encodingKeys)
    {
        var hits = plan.JvmArguments.Count(a => string.Equals(JvmPropertyArguments.PropertyName(a), key, StringComparison.OrdinalIgnoreCase));
        Check($"-D{key} 恰好一条", hits == 1, $"实际 {hits} 条");
    }

    // 用户显式给了 GBK：以用户为准，不再追加 UTF-8
    var gbkPlan = LaunchPlanBuilder.Build(version, ctx, launcher with { ExtraJvmArgs = "-Dfile.encoding=GBK" }, @"C:\java\bin\java.exe");
    var fileEncoding = gbkPlan.JvmArguments.Where(a => a.StartsWith("-Dfile.encoding=", StringComparison.Ordinal)).ToList();
    Check("用户给 -Dfile.encoding=GBK 时不覆盖也不重复", fileEncoding.Count == 1 && fileEncoding[0] == "-Dfile.encoding=GBK", string.Join(" | ", fileEncoding));

    // 大小写写错也算已声明（JVM 认大小写，但叠两条只会更难排查）
    var weirdCase = LaunchPlanBuilder.Build(version, ctx, launcher with { ExtraJvmArgs = "-DFile.Encoding=GBK" }, @"C:\java\bin\java.exe");
    Check("属性名判重大小写不敏感", weirdCase.JvmArguments.Count(a => a.StartsWith("-Dfile.encoding=", StringComparison.OrdinalIgnoreCase)) == 1,
        string.Join(" | ", weirdCase.JvmArguments.Where(a => a.StartsWith("-D", StringComparison.Ordinal))));

    // 版本 JSON 自己声明了就不重复
    var versionDeclared = new VersionJson
    {
        Id = "1.12.2",
        MainClass = "net.minecraft.client.main.Main",
        Libraries = [],
        Arguments = new Arguments
        {
            Jvm = ["-Dfile.encoding=GBK", "-Djava.library.path=${natives_directory}", "-cp", "${classpath}"],
            Game = ["--username", "${auth_player_name}"],
        },
    };
    var declaredPlan = LaunchPlanBuilder.Build(versionDeclared, ctx, launcher, @"C:\java\bin\java.exe");
    Check("版本 JSON 声明过的编码属性不重复追加",
        declaredPlan.JvmArguments.Count(a => a.StartsWith("-Dfile.encoding=", StringComparison.OrdinalIgnoreCase)) == 1
        && declaredPlan.JvmArguments.Contains("-Dfile.encoding=GBK"));

    Check("非 -D 参数不参与属性判重", JvmPropertyArguments.PropertyName("-Xmx2G") is null);
    Check("-D 无值也能取到属性名", JvmPropertyArguments.PropertyName("-Dfoo") == "foo");
    Check("裸 -D 不算属性", JvmPropertyArguments.PropertyName("-D") is null);
    Check("不改 -Xmx/-Xms", plan.JvmArguments[0] == "-Xmx4096M" && plan.JvmArguments[1] == "-Xms1024M", string.Join(" | ", plan.JvmArguments.Take(2)));
}

Console.WriteLine("== 19. 内存自适应默认值 ==");
{
    const long Mb = 1024L * 1024L;

    Check("2GB → 2048", InstanceSettings.DefaultMemoryMb(2048 * Mb) == 2048);
    Check("4GB → 2048（低于下界时抬到 2G）", InstanceSettings.DefaultMemoryMb(4096 * Mb) == 2048);
    Check("8GB → 2048", InstanceSettings.DefaultMemoryMb(8192 * Mb) == 2048);
    Check("16GB → 4096", InstanceSettings.DefaultMemoryMb(16384 * Mb) == 4096);
    Check("32GB → 8192", InstanceSettings.DefaultMemoryMb(32768 * Mb) == 8192);
    Check("64GB → 16384（封顶）", InstanceSettings.DefaultMemoryMb(65536 * Mb) == 16384);
    Check("512GB 也封顶 16384", InstanceSettings.DefaultMemoryMb(524288 * Mb) == 16384);
    Check("1GB 物理内存抬到 2048", InstanceSettings.DefaultMemoryMb(1024 * Mb) == 2048);
    Check("总内存为 0 时退回 2048", InstanceSettings.DefaultMemoryMb(0) == 2048);
    Check("9000MB → 2250 向下取整到 2048", InstanceSettings.DefaultMemoryMb(9000 * Mb) == 2048, InstanceSettings.DefaultMemoryMb(9000 * Mb).ToString());

    var adaptive = InstanceSettings.AdaptiveMemoryMb();
    Check("自适应值在 [2048, 16384] 且是 512 的倍数",
        adaptive is >= InstanceSettings.MinAdaptiveMemoryMb and <= InstanceSettings.MaxAdaptiveMemoryMb
        && adaptive % InstanceSettings.MemoryStepMb == 0, adaptive.ToString());

    var dir = NewTempTree("settings");
    try
    {
        var instanceId = "auto";
        var settingsPath = InstanceSettings.PathFor(dir, instanceId);
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);

        Check("没有 settings.json 时走自适应", InstanceSettings.Load(dir, "missing").MemoryMb == adaptive);

        File.WriteAllText(settingsPath, "{\"MemoryMb\": 4096}");
        Check("显式写 4096 就还是 4096", InstanceSettings.Load(dir, instanceId).MemoryMb == 4096);

        File.WriteAllText(settingsPath, "{\"JvmPreset\": \"fast\"}");
        Check("字段缺失时走自适应", InstanceSettings.Load(dir, instanceId).MemoryMb == adaptive);
        Check("字段缺失不影响其它设置读入", InstanceSettings.Load(dir, instanceId).JvmPreset == JvmPreset.Fast);

        File.WriteAllText(settingsPath, "{\"MemoryMb\": null}");
        Check("MemoryMb 为 null 视为缺失", InstanceSettings.Load(dir, instanceId).MemoryMb == adaptive);

        File.WriteAllText(settingsPath, "{ 这不是 json");
        Check("坏档退回自适应且不抛", InstanceSettings.Load(dir, instanceId).MemoryMb == adaptive);

        File.WriteAllText(settingsPath, "{\"MemoryMb\": 8192}");
        var reloaded = InstanceSettings.Load(dir, instanceId).Normalized();
        Check("正常存档原样读回并 Normalized", reloaded.MemoryMb == 8192 && reloaded.WindowWidth == 1280);
    }
    finally { TryDelete(dir); }
}

Console.WriteLine("== 20. natives 缓存复用 ==");
{
    var root = NewTempTree("natives");
    try
    {
        var libDir = Path.Combine(root, "libraries", "org", "lwjgl", "lwjgl-platform", "2.9.4");
        var jarA = WriteNativeJar(libDir, "lwjgl-platform-2.9.4-natives-windows.jar",
            "lwjgl.dll", "windows/x64/OpenAL64.dll", "META-INF/MANIFEST.MF");
        var jarB = WriteNativeJar(libDir, "jinput-platform-2.9.4-natives-windows.jar", "jinput-dx8_64.dll");
        string[] jars = [jarA, jarB];

        var fingerprint = NativesProvisioner.Fingerprint(jars);
        Check("指纹是 16 位十六进制", fingerprint.Length == 16 && fingerprint.All(Uri.IsHexDigit), fingerprint);

        var first = NativesProvisioner.Provision(root, "1.12.2", jars);
        Check("首次解压不标记为复用", !first.Reused);
        Check("首次解压文件计数正确（3 个 dll）", first.ExtractedFiles == 3 && first.Files == 3, $"{first.ExtractedFiles}/{first.Files}");
        Check("缓存目录形如 natives/<versionId>/<指纹>",
            first.Directory == NativesProvisioner.ResolveDirectory(root, "1.12.2", fingerprint), first.Directory);
        Check("dll 已落地", File.Exists(Path.Combine(first.Directory, "lwjgl.dll")) && File.Exists(Path.Combine(first.Directory, "OpenAL64.dll")));
        Check("jar 内子目录被拍平", File.Exists(Path.Combine(first.Directory, "OpenAL64.dll")));
        Check("META-INF 不解压", !Directory.Exists(Path.Combine(first.Directory, "META-INF")));
        Check("已写完成标记", File.Exists(Path.Combine(first.Directory, NativesProvisioner.MarkerFileName)));
        Check("没留下 .staging 目录",
            !Directory.EnumerateDirectories(Path.GetDirectoryName(first.Directory)!)
                .Any(d => Path.GetFileName(d).StartsWith(".staging-", StringComparison.Ordinal)));

        var markerPath = Path.Combine(first.Directory, NativesProvisioner.MarkerFileName);
        var markerStamp = File.GetLastWriteTimeUtc(markerPath);

        var second = NativesProvisioner.Provision(root, "1.12.2", jars);
        Check("第二次命中缓存", second.Reused);
        Check("第二次不再解压（计数 0）", second.ExtractedFiles == 0, second.ExtractedFiles.ToString());
        Check("第二次目录不变", second.Directory == first.Directory);
        Check("第二次没重写完成标记", File.GetLastWriteTimeUtc(markerPath) == markerStamp,
            $"{File.GetLastWriteTimeUtc(markerPath):O} vs {markerStamp:O}");

        // 缓存目录被破坏：少一个 dll → 自愈重建
        File.Delete(Path.Combine(first.Directory, "OpenAL64.dll"));
        var healed = NativesProvisioner.Provision(root, "1.12.2", jars);
        Check("缺文件时自愈重建", !healed.Reused && healed.ExtractedFiles == 3);
        Check("重建后文件回来", File.Exists(Path.Combine(healed.Directory, "OpenAL64.dll")));

        // 完成标记丢了但文件还在 → 也当不完整，重解
        File.Delete(Path.Combine(healed.Directory, NativesProvisioner.MarkerFileName));
        var noMarker = NativesProvisioner.Provision(root, "1.12.2", jars);
        Check("完成标记缺失时重解", !noMarker.Reused && noMarker.ExtractedFiles == 3);

        // 目录里只有半成品且没有标记 → 整体替换，垃圾文件不许留
        File.Delete(Path.Combine(noMarker.Directory, NativesProvisioner.MarkerFileName));
        File.Delete(Path.Combine(noMarker.Directory, "lwjgl.dll"));
        File.WriteAllText(Path.Combine(noMarker.Directory, "junk.bin"), "half-extracted");
        var replaced = NativesProvisioner.Provision(root, "1.12.2", jars);
        Check("不完整的旧目录被整体替换", !replaced.Reused && !File.Exists(Path.Combine(replaced.Directory, "junk.bin")));
        Check("替换后 natives 完整", replaced.Files == 3 && replaced.ExtractedFiles == 3);

        // native jar 变了（重下/换版本）→ 指纹变、目录变
        File.AppendAllText(jarB, "// bumped");
        var bumped = NativesProvisioner.Fingerprint(jars);
        Check("jar 变化会换指纹", bumped != fingerprint, $"{fingerprint} → {bumped}");
        var newDir = NativesProvisioner.Provision(root, "1.12.2", jars);
        Check("换指纹后落到新目录", newDir.Directory != first.Directory && !newDir.Reused);
        Check("旧缓存仍在（不破坏并发实例）", Directory.Exists(first.Directory));

        // 没有 native jar 的现代版本：不该创建任何目录
        var none = NativesProvisioner.Provision(root, "1.21.4", []);
        Check("没有 native jar 时跳过解压", none.JarCount == 0 && none.Reused && none.ExtractedFiles == 0);
        Check("没有 native jar 时退回历史路径", none.Directory == NativesProvisioner.LegacyDirectory(root, "1.21.4"));

        // jar 根本不存在：给出可读错误，而不是 NullReference
        var missingJarFailed = false;
        try
        {
            NativesProvisioner.Provision(root, "9.9.9", [Path.Combine(root, "does-not-exist.jar")]);
        }
        catch (InvalidOperationException)
        {
            missingJarFailed = true;
        }
        Check("jar 缺失时报 InvalidOperationException", missingJarFailed);

        // 和 LaunchPlanBuilder / LaunchPreflight 对齐：三处必须用同一个 natives 目录
        var libRoot = Path.Combine(root, "libraries");
        var clientJar = Path.Combine(root, "versions", "1.12.2", "1.12.2.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(clientJar)!);
        File.WriteAllText(clientJar, "");

        var nativeVersion = new VersionJson
        {
            Id = "1.12.2",
            MainClass = "net.minecraft.client.main.Main",
            Libraries =
            [
                new Library
                {
                    Name = "org.lwjgl:lwjgl-platform:2.9.4",
                    Natives = new Dictionary<string, string> { ["windows"] = "natives-windows" },
                    Downloads = new LibraryDownloads
                    {
                        Classifiers = new Dictionary<string, DownloadArtifact>
                        {
                            ["natives-windows"] = new DownloadArtifact
                            {
                                Path = "org/lwjgl/lwjgl-platform/2.9.4/lwjgl-platform-2.9.4-natives-windows.jar",
                                Url = "https://example.invalid/lwjgl-natives.jar",
                            },
                        },
                    },
                },
            ],
        };

        var cp = ClassPathBuilder.Build(nativeVersion, ctx, libRoot, clientJar);
        Check("老版本 natives jar 被单独挑出来", cp.NativeJars.Count == 1, string.Join(" | ", cp.NativeJars));
        Check("natives jar 不混进 classpath", cp.Entries.All(e => !e.Contains("natives-windows", StringComparison.Ordinal)));

        var provision = NativesProvisioner.Provision(root, nativeVersion.Id, cp.NativeJars);
        var nativeLaunch = launcher with
        {
            NativesDirectory = provision.Directory,
            LibrariesRoot = libRoot,
            ClientJarPath = clientJar,
            GameDirectory = Path.Combine(root, "instances", "1.12.2", ".minecraft"),
        };

        var nativePlan = LaunchPlanBuilder.Build(nativeVersion, ctx, nativeLaunch, @"C:\java\bin\java.exe");
        Check("plan 的 java.library.path 指向缓存目录",
            nativePlan.JvmArguments.Any(a => a.StartsWith("-Djava.library.path=", StringComparison.Ordinal) && a.EndsWith(provision.Fingerprint, StringComparison.Ordinal)),
            string.Join(" | ", nativePlan.JvmArguments.Where(a => a.StartsWith("-Djava.library.path=", StringComparison.Ordinal))));

        var nativeReport = await LaunchPreflight.CheckAsync(nativeVersion, ctx, nativeLaunch);
        Check("预检与 plan 看同一份 natives", nativeReport.NativesDirectory == provision.Directory, nativeReport.NativesDirectory);
        Check("已解压时预检不报 natives 缺口",
            nativeReport.IsReady && nativeReport.Gaps.All(g => g.Kind != PreflightGapKind.NativesDirectory), nativeReport.Describe());

        // 一条调用走完「算 classpath → 解压 natives → 回填目录」，App 侧接这一行就够
        var rawLaunch = launcher with
        {
            LibrariesRoot = libRoot,
            ClientJarPath = clientJar,
            GameDirectory = Path.Combine(root, "instances", "1.12.2", ".minecraft"),
        };
        var prepared = await LaunchPreparer.PrepareAsync(nativeVersion, ctx, rawLaunch, root);
        Check("PrepareAsync 回填 natives 目录", prepared.Context.NativesDirectory == provision.Directory,
            prepared.Context.NativesDirectory ?? "(null)");
        var preparedPlan = LaunchPlanBuilder.Build(nativeVersion, ctx, prepared.Context, @"C:\java\bin\java.exe");
        Check("PrepareAsync 后 plan 的 java.library.path 指向缓存",
            preparedPlan.JvmArguments.Any(a => a.StartsWith("-Djava.library.path=", StringComparison.Ordinal)
                && a.EndsWith(prepared.Natives.Fingerprint, StringComparison.Ordinal)));
        var preparedAgain = await LaunchPreparer.PrepareAsync(nativeVersion, ctx, prepared.Context, root);
        Check("第二次 PrepareAsync 直接复用",
            preparedAgain.Natives.Reused && preparedAgain.Context.NativesDirectory == prepared.Context.NativesDirectory);
    }
    finally { TryDelete(root); }
}

Console.WriteLine("== 21. 整合包导入回滚：只清理本次新建的目录 ==");
{
    var root = NewTempTree("modpack-rollback");
    try
    {
        // 既有状态：装过的原版、别人的实例、跨实例共享的库
        var vanillaJson = Path.Combine(root, "versions", "1.20.1", "1.20.1.json");
        Directory.CreateDirectory(Path.GetDirectoryName(vanillaJson)!);
        await File.WriteAllTextAsync(vanillaJson, "{\"id\":\"1.20.1\"}");

        var keepInstance = Path.Combine(root, "instances", "keep-me");
        Directory.CreateDirectory(keepInstance);
        await File.WriteAllTextAsync(Path.Combine(keepInstance, "instance.json"), "{}");

        var library = Path.Combine(root, "libraries", "org.example", "lib.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(library)!);
        await File.WriteAllTextAsync(library, "lib");

        // 场景 A：Forge installer 把 versions/<id>/ 建了一半再失败 → 这个新版本目录必须回滚
        const string forgeVersionId = "1.20.1-forge-47.4.18";
        var forgePack = new ModrinthPack
        {
            Name = "回滚测试包A",
            VersionId = "1.0.0",
            Dependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["minecraft"] = "1.20.1",
                ["forge"] = "47.4.18",
            },
        };

        var installerA = BuildModpackInstaller(
            new FakeDownloader((_, _) => null),
            new FailingForgeInstaller(forgeVersionId),
            new ThrowingNeoForgeInstaller());

        Exception? thrownA = null;
        try { await installerA.ImportAsync(new FakeModpackSource(forgePack), root); }
        catch (Exception ex) { thrownA = ex; }

        Check("回滚A：抛的是 ModpackImportFailedException", thrownA is ModpackImportFailedException, thrownA?.GetType().Name);
        Check("回滚A：原始异常保留在 InnerException", thrownA?.InnerException is InvalidOperationException, thrownA?.InnerException?.GetType().Name);
        Check("回滚A：本次新建的版本目录已删除", !Directory.Exists(Path.Combine(root, "versions", forgeVersionId)));
        Check("回滚A：报告里列出删掉了哪些目录",
            thrownA is ModpackImportFailedException { RolledBack: true } rolledA
            && rolledA.RemovedPaths.Any(p => p.Contains(forgeVersionId, StringComparison.OrdinalIgnoreCase)),
            thrownA is ModpackImportFailedException ra ? string.Join(" | ", ra.RemovedPaths) : null);
        Check("回滚A：既有原版版本没被误删", File.Exists(vanillaJson));
        Check("回滚A：别的实例没被误删", Directory.Exists(keepInstance));
        Check("回滚A：共享 libraries 没被删", File.Exists(library));

        // 场景 B：一路顺利到 overrides 才失败 → 本次新建的 instances/<id> 回滚，原本就在的版本目录保留
        var plainPack = new ModrinthPack
        {
            Name = "回滚测试包B",
            VersionId = "1.0.0",
            Dependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["minecraft"] = "1.20.1" },
        };

        var installerB = BuildModpackInstaller(
            new FakeDownloader((_, _) => null), new ThrowingForgeInstaller(), new ThrowingNeoForgeInstaller());

        var failingSource = new FakeModpackSource(
            plainPack,
            (_, _, _) => Task.FromException<int>(new InvalidOperationException("模拟 overrides 解压失败")));

        Exception? thrownB = null;
        try { await installerB.ImportAsync(failingSource, root); }
        catch (Exception ex) { thrownB = ex; }

        Check("回滚B：本次新建的实例目录已删除", !Directory.Exists(Path.Combine(root, "instances", "1.20.1")));
        Check("回滚B：原本就有的版本目录保留", File.Exists(vanillaJson));
        Check("回滚B：异常信息点名真实原因",
            thrownB?.Message.Contains("overrides 解压失败", StringComparison.Ordinal) == true,
            thrownB?.Message.Split(Environment.NewLine)[0]);
    }
    finally { TryDelete(root); }
}

Console.WriteLine("== 22. client-overrides 覆盖 overrides（CurseForge 规范）==");
{
    var tree = NewTempTree("modpack-overrides");
    try
    {
        var packPath = Path.Combine(tree, "pack.mrpack");
        CreateZipFile(packPath,
        [
            ("modrinth.index.json", """{"formatVersion":1,"game":"minecraft","versionId":"1.0.0","name":"覆盖测试","dependencies":{"minecraft":"1.20.1"},"files":[]}"""),
            ("overrides/config/app.toml", "from-overrides"),
            ("overrides/config/only-base.toml", "base-only"),
            ("overrides/mods/base.jar", "jar-base"),
            ("client-overrides/config/app.toml", "from-client-overrides"),
        ]);

        var target = Path.Combine(tree, "instance", ".minecraft");
        var written = 0;
        await using (var source = ZipModpackSource.Open(packPath))
            written = await source.ExtractOverridesAsync(target);

        Check("覆盖：同名文件最终是 client-overrides 的内容",
            File.ReadAllText(Path.Combine(target, "config", "app.toml")) == "from-client-overrides");
        Check("覆盖：只存在于 overrides 的文件保留",
            File.ReadAllText(Path.Combine(target, "config", "only-base.toml")) == "base-only");
        Check("覆盖：子目录里的非同名文件也在",
            File.ReadAllText(Path.Combine(target, "mods", "base.jar")) == "jar-base");
        Check("覆盖：两层都写（共 4 次）", written == 4, written.ToString());
    }
    finally { TryDelete(tree); }
}

Console.WriteLine("== 23. 300+ 字符长路径：解压、读取、清理 ==");
{
    var tree = NewTempTree("modpack-longpath");
    try
    {
        var deep = string.Join('/', Enumerable.Repeat("d1234567", 40)); // 359 字符的深层目录
        var packPath = Path.Combine(tree, "long.mrpack");
        CreateZipFile(packPath,
        [
            ("modrinth.index.json", """{"formatVersion":1,"game":"minecraft","versionId":"1.0.0","name":"长路径测试","dependencies":{"minecraft":"1.20.1"},"files":[]}"""),
            ($"overrides/config/{deep}/deep.txt", "deep-content"),
        ]);

        var target = Path.Combine(tree, "instances", "long", ".minecraft");
        var written = 0;
        await using (var source = ZipModpackSource.Open(packPath))
            written = await source.ExtractOverridesAsync(target);

        var dest = Path.Combine(target, "config", deep.Replace('/', Path.DirectorySeparatorChar), "deep.txt");
        Check("长路径：整条路径超过 MAX_PATH", dest.Length > LongPath.LegacyMaxPath, dest.Length.ToString());
        Check("长路径：解压成功（1 个文件）", written == 1, written.ToString());
        Check("长路径：文件真的落盘", LongPath.FileExists(dest));
        Check("长路径：内容正确", File.ReadAllText(LongPath.Normalize(dest)) == "deep-content");
        Check("长路径：超限路径加上 \\\\?\\ 前缀",
            LongPath.Normalize(dest).StartsWith(@"\\?\", StringComparison.Ordinal));
        Check("长路径：短路径不加前缀", LongPath.Normalize(@"C:\a\b.txt") == @"C:\a\b.txt");
        Check("长路径：UNC 走 \\\\?\\UNC 前缀",
            LongPath.Normalize(@"\\server\share\" + new string('x', 300)).StartsWith(@"\\?\UNC\", StringComparison.Ordinal));

        LongPath.DeleteDirectory(target);
        Check("长路径：整棵树能清理干净", !Directory.Exists(target) && !LongPath.FileExists(dest));

        // 相对段归一化是同一处实现：zip slip 必须仍然被挡住
        var slipPath = Path.Combine(tree, "slip.mrpack");
        CreateZipFile(slipPath,
        [
            ("modrinth.index.json", """{"formatVersion":1,"game":"minecraft","versionId":"1.0.0","name":"越界测试","dependencies":{"minecraft":"1.20.1"},"files":[]}"""),
            ("overrides/../../../evil.txt", "x"),
        ]);

        var slipBlocked = false;
        await using (var slip = ZipModpackSource.Open(slipPath))
        {
            try { await slip.ExtractOverridesAsync(Path.Combine(tree, "slip-out")); }
            catch (InvalidDataException) { slipBlocked = true; }
        }

        Check("长路径：zip slip 仍被拦住", slipBlocked);
    }
    finally { TryDelete(tree); }
}

Console.WriteLine("== 24. 待补全清单：下载失败 / 部分成功 ==");
{
    var root = NewTempTree("modpack-missing");
    try
    {
        var vanillaJson = Path.Combine(root, "versions", "1.20.1", "1.20.1.json");
        Directory.CreateDirectory(Path.GetDirectoryName(vanillaJson)!);
        await File.WriteAllTextAsync(vanillaJson, "{\"id\":\"1.20.1\"}");

        const string url = "https://example.invalid/mods/x.jar";
        var pack = new ModrinthPack
        {
            Name = "清单测试包",
            VersionId = "1.0.0",
            Dependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["minecraft"] = "1.20.1" },
            Files =
            [
                new ModpackFile("mods/x.jar", 4096, "sha1-x", null, [url], ClientRequired: true),
            ],
        };

        // ① 下载整批失败：异常里要能读到缺哪个文件、从哪下、多大
        var installer = BuildModpackInstaller(
            new FailingDownloader(new AggregateException(new IOException($"下载失败（3 次尝试）：{url}"))),
            new ThrowingForgeInstaller(),
            new ThrowingNeoForgeInstaller());

        Exception? thrown = null;
        try { await installer.ImportAsync(new FakeModpackSource(pack), root); }
        catch (Exception ex) { thrown = ex; }

        Check("清单：下载失败时抛 ModpackImportFailedException", thrown is ModpackImportFailedException, thrown?.GetType().Name);
        if (thrown is ModpackImportFailedException failure)
        {
            Check("清单：记下缺哪个文件",
                failure.MissingFiles.Count == 1 && failure.MissingFiles[0].RelativePath == "mods/x.jar",
                string.Join(" | ", failure.MissingFiles.Select(f => f.RelativePath)));
            Check("清单：带 URL 与大小",
                failure.MissingFiles[0].Url == url && failure.MissingFiles[0].Size == 4096,
                failure.MissingFiles[0].Describe());
            Check("清单：消息里能直接读到文件 / URL / 原因",
                failure.Message.Contains("mods/x.jar", StringComparison.Ordinal)
                && failure.Message.Contains(url, StringComparison.Ordinal)
                && failure.Message.Contains("待补全", StringComparison.Ordinal));
            Check("清单：失败后本次新建的实例目录已回滚",
                !Directory.Exists(Path.Combine(root, "instances", "1.20.1")));
        }

        // ② 部分成功：下载器报"处理过"但没落盘 → 报告与进度里都要有清单
        var lazyInstaller = BuildModpackInstaller(
            new FakeDownloader((_, _) => null), new ThrowingForgeInstaller(), new ThrowingNeoForgeInstaller());
        var recorder = new RecordingProgress<ModpackImportProgress>();
        var report = await lazyInstaller.ImportAsync(new FakeModpackSource(pack), root, recorder);

        Check("清单：部分成功也进报告", report.MissingCount == 1 && !report.Complete, report.MissingCount.ToString());
        Check("清单：摘要一行可读", report.MissingSummary?.Contains("mods/x.jar", StringComparison.Ordinal) == true, report.MissingSummary);
        Check("清单：进度回调里有「待补全」阶段",
            recorder.Items.Any(p => p.Stage == "待补全" && (p.Detail?.Contains("mods/x.jar", StringComparison.Ordinal) ?? false)),
            string.Join(" | ", recorder.Items.Select(p => p.Stage).Distinct()));
        Check("清单：缺文件时实例目录仍然建好（可复用）",
            Directory.Exists(Path.Combine(root, "instances", "1.20.1", ".minecraft")));
    }
    finally { TryDelete(root); }
}

Console.WriteLine("== 25. 本地 mod 扫描：元数据 / 启用状态 / 重复 modId ==");
{
    var root = NewTempTree("modscan");
    try
    {
        var modsDir = Path.Combine(root, "mods");
        Directory.CreateDirectory(modsDir);

        const string forgeTomlTemplate = """
            modLoader="javafml"
            loaderVersion="[47,)"
            license="MIT"

            [[mods]]
            modId="examplemod"
            version="VERSION"
            displayName="Example Mod"
            """;

        CreateZipFile(Path.Combine(modsDir, "a-example.jar"),
            [("META-INF/mods.toml", forgeTomlTemplate.Replace("VERSION", "1.2.3", StringComparison.Ordinal))]);
        CreateZipFile(Path.Combine(modsDir, "b-example-old.jar"),
            [("META-INF/mods.toml", forgeTomlTemplate.Replace("VERSION", "1.2.2", StringComparison.Ordinal))]);
        CreateZipFile(Path.Combine(modsDir, "c-fabric.jar"),
            [("fabric.mod.json", """{"schemaVersion":1,"id":"fabricmod","version":"0.5.0","name":"Fabric Mod"}""")]);
        File.WriteAllText(Path.Combine(modsDir, "d-broken.jar"), "definitely not a zip file");
        CreateZipFile(Path.Combine(modsDir, "e-example.jar.disabled"),
            [("META-INF/mods.toml", forgeTomlTemplate.Replace("VERSION", "1.0.0", StringComparison.Ordinal))]);
        File.WriteAllText(Path.Combine(modsDir, "notes.txt"), "说明文件，不是 mod");

        var scan = InstalledModScanner.Scan(modsDir);
        var byName = scan.Mods.ToDictionary(m => m.FileName, StringComparer.Ordinal);

        Check("扫描：只列 jar 与 jar.disabled，忽略其它文件", scan.Mods.Count == 5, scan.Mods.Count.ToString());
        Check("扫描：按文件名升序稳定输出",
            string.Join(",", scan.Mods.Select(m => m.FileName))
            == "a-example.jar,b-example-old.jar,c-fabric.jar,d-broken.jar,e-example.jar.disabled",
            string.Join(",", scan.Mods.Select(m => m.FileName)));
        Check("扫描：mods.toml 读出 modId / 名字 / 版本 / 加载器",
            byName["a-example.jar"] is { ModId: "examplemod", Name: "Example Mod", Version: "1.2.3", LoaderKind: "forge", Enabled: true },
            byName["a-example.jar"].Describe());
        Check("扫描：fabric.mod.json 走另一套元数据",
            byName["c-fabric.jar"] is { ModId: "fabricmod", Name: "Fabric Mod", Version: "0.5.0", LoaderKind: "fabric" },
            byName["c-fabric.jar"].Describe());
        Check("扫描：坏包不抛，ModId 为 null，名字退回文件名",
            byName["d-broken.jar"] is { ModId: null, Name: "d-broken", Enabled: true },
            byName["d-broken.jar"].Describe());
        Check("扫描：.jar.disabled 记为未启用但仍读元数据",
            byName["e-example.jar.disabled"] is { Enabled: false, ModId: "examplemod", Version: "1.0.0", Name: "Example Mod" },
            byName["e-example.jar.disabled"].Describe());
        Check("扫描：启用 / 停用计数",
            scan.EnabledCount == 4 && scan.DisabledCount == 1,
            $"{scan.EnabledCount}/{scan.DisabledCount}");
        Check("扫描：同一 modId 两个以上文件算重复（含 disabled 那个）",
            scan.DuplicateModIdCount == 1,
            scan.DuplicateModIdCount?.ToString() ?? "null");
        Check("扫描：重复明细给出被撞的 modId",
            scan.DuplicateModIds.SequenceEqual(["examplemod"]),
            string.Join(",", scan.DuplicateModIds));
        Check("扫描：大小累加等于逐项之和",
            scan.TotalBytes == scan.Mods.Sum(m => m.Bytes) && scan.TotalBytes > 0,
            scan.TotalBytes.ToString());

        Check("扫描：目录不存在时返回空结果，不抛",
            InstalledModScanner.Scan(Path.Combine(root, "no-such-dir")) is { } none && none.Mods.Count == 0 && none.DuplicateModIdCount == 0);

        var emptyDir = Path.Combine(root, "empty-mods");
        Directory.CreateDirectory(emptyDir);
        Check("扫描：空目录返回空结果", InstalledModScanner.Scan(emptyDir).Mods.Count == 0);

        var cancelled = false;
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            InstalledModScanner.Scan(modsDir, cts.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        Check("扫描：已取消的 token 立刻中断", cancelled);
    }
    finally { TryDelete(root); }
}

Console.WriteLine("== 26. 更新检查：Modrinth 优先 / CurseForge 兜底 / 并发与取消 ==");
{
    var root = NewTempTree("modupdate");
    try
    {
        const string instanceId = "fabric-loader-0.15.11-1.20.1";
        var modsDir = Path.Combine(root, "instances", instanceId, ".minecraft", "mods");
        Directory.CreateDirectory(modsDir);

        void Put(string fileName, string id, string version, string name) =>
            CreateZipFile(Path.Combine(modsDir, fileName),
                [("fabric.mod.json",
                    $$"""{"schemaVersion":1,"id":"{{id}}","version":"{{version}}","name":"{{name}}"}""")]);

        Put("updatable.jar", "updatablemod", "1.0.0", "Updatable");
        Put("current.jar", "currentmod", "2.0.0", "Current");
        Put("cfonly.jar", "12345", "3.0.0", "CfOnly");
        Put("cfresolved.jar", "54321", "1.0.0", "CfResolved");
        Put("ghost.jar", "ghostmod", "1.0.0", "Ghost");

        var modrinth = new StubModrinthClient
        {
            DelayMs = 15,
            Versions = new(StringComparer.Ordinal)
            {
                ["updatablemod"] = [StubModrinthClient.Version("updatablemod", "1.4.0")],
                ["currentmod"] = [StubModrinthClient.Version("currentmod", "2.0.0")],
            },
        };

        var curseForge = new StubCurseForgeClient
        {
            Files = new()
            {
                [12345] =
                [
                    new CfFile
                    {
                        Id = 991,
                        ModId = 12345,
                        FileName = "cfonly-3.1.0.jar",
                        DisplayName = "CfOnly 3.1.0",
                        FileLength = 4321,
                        DownloadUrl = "https://cdn.example/cfonly-3.1.0.jar",
                    },
                ],
                [54321] =
                [
                    new CfFile
                    {
                        Id = 992,
                        ModId = 54321,
                        FileName = "cfresolved-1.2.0.jar",
                        DisplayName = "CfResolved 1.2.0",
                        FileLength = 2048,
                    },
                ],
            },
        };

        var origins = new Dictionary<string, ModOrigin>();
        foreach (var (file, source, project, version) in new[]
        {
            ("updatable.jar", "modrinth", "updatablemod", "1.0.0"), ("current.jar", "modrinth", "currentmod", "2.0.0"),
            ("cfonly.jar", "curseforge", "12345", "3.0.0"), ("cfresolved.jar", "curseforge", "54321", "1.0.0"),
        }) origins[file] = new(source, project, "old", version, await ModSourceStore.HashAsync(Path.Combine(modsDir, file)), new FileInfo(Path.Combine(modsDir, file)).Length);
        await ModSourceStore.SaveAsync(modsDir, origins);
        var service = new ModUpdateService(modrinth, curseForge);
        var recorder = new RecordingProgress<ModInstallProgress>();
        var report = await service.CheckAsync(root, instanceId, DownloadCategory.Mod, recorder);
        var byFile = report.Candidates.ToDictionary(c => c.FileName, StringComparer.Ordinal);

        Check("更新：候选与扫描结果一一对应", report.Candidates.Count == 5, report.Candidates.Count.ToString());
        Check("更新：Modrinth 有新版 → HasUpdate 且带下载地址",
            byFile["updatable.jar"] is { HasUpdate: true, LatestVersion: "1.4.0", Source: "modrinth", DownloadUrl: not null },
            byFile["updatable.jar"].Describe());
        Check("更新：版本相同 → 不算更新",
            byFile["current.jar"] is { HasUpdate: false, LatestVersion: "2.0.0", Source: "modrinth" },
            byFile["current.jar"].Describe());
        Check("更新：按记录的 CurseForge 项目查询",
            byFile["cfonly.jar"] is { HasUpdate: true, LatestVersion: "3.1.0", Source: "curseforge" },
            byFile["cfonly.jar"].Describe());
        Check("更新：CF 没给 DownloadUrl 时换临时下载地址",
            byFile["cfresolved.jar"].DownloadUrl == "https://cdn.example/cf-resolved.jar",
            byFile["cfresolved.jar"].DownloadUrl);
        Check("更新：两个源都不通 → 记失败但不抛",
            byFile["ghost.jar"] is { Source: "none", HasUpdate: false },
            byFile["ghost.jar"].Describe());
        Check("更新：计数自洽（更新 3 / 查到 4 / 失败 1）",
            report.UpdateCount == 3 && report.CheckedCount == 4 && report.FailedCount == 1,
            $"{report.UpdateCount}/{report.CheckedCount}/{report.FailedCount}");
        Check("更新：查询带上实例的 MC 版本与加载器",
            modrinth.Seen.Contains("updatablemod|1.20.1|fabric"),
            string.Join(" ; ", modrinth.Seen));
        Check("更新：进度推进到 100%",
            recorder.Items.Count > 0 && Math.Abs(recorder.Items[^1].Percent - 100) < 0.001,
            recorder.Items.Count == 0 ? "没有进度" : recorder.Items[^1].Percent.ToString("F1"));
        Check("更新：并发不超过 4（且确实并行）",
            modrinth.MaxConcurrent is >= 2 and <= ModUpdateService.MaxConcurrency,
            modrinth.MaxConcurrent.ToString());

        var slow = new StubModrinthClient { DelayMs = 300 };
        var slowService = new ModUpdateService(slow, new StubCurseForgeClient());
        var cancelled = false;
        try
        {
            using var cts = new CancellationTokenSource(30);
            await slowService.CheckAsync(root, instanceId, DownloadCategory.Mod, null, cts.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        Check("更新：ct 取消能中断在飞请求", cancelled);
    }
    finally { TryDelete(root); }
}

Console.WriteLine("== 27. 实例身份解耦：同加载器两个包 / 旧数据迁移 ==");
{
    var root = NewTempTree("instanceregistry");
    try
    {
        const string versionId = "fabric-loader-0.20.1-1.21.4";
        const string profileJson = """
            {"id":"fabric-loader-0.20.1-1.21.4","inheritsFrom":"1.21.4","mainClass":"net.fabricmc.loader.impl.launch.knot.KnotClient","libraries":[],"arguments":{"jvm":[],"game":[]}}
            """;

        // 原版与加载器都"已经装好"：导入流程不该回源（StubVersionMeta 一律抛）
        void WriteVersionJson(string id)
        {
            var directory = Path.Combine(root, "versions", id);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, id + ".json"),
                $$"""{"id":"{{id}}","type":"release","mainClass":"net.minecraft.client.main.Main"}""");
        }

        WriteVersionJson("1.21.4");

        var downloader = new FakeDownloader((_, _) => null);
        var gameInstaller = new GameInstaller(downloader, new StubVersionMeta());
        var loaderMeta = new FixedLoaderMeta("0.20.1", profileJson);
        var loaderInstaller = new LoaderInstaller(loaderMeta, new StubVersionMeta(), gameInstaller);
        var installer = new ModpackInstaller(
            gameInstaller, loaderInstaller, new ThrowingForgeInstaller(), new ThrowingNeoForgeInstaller(), downloader);

        ModrinthPack Pack(string name, string version) => new()
        {
            Name = name,
            VersionId = version,
            SourceLabel = "Modrinth",
            Dependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["minecraft"] = "1.21.4",
                ["fabric-loader"] = "0.20.1",
            },
        };

        var first = await installer.ImportAsync(new FakeModpackSource(Pack("整合包甲", "1.0.0")), root);
        var second = await installer.ImportAsync(new FakeModpackSource(Pack("整合包乙", "2.0.0")), root);
        var repeat = await installer.ImportAsync(new FakeModpackSource(Pack("整合包甲", "1.0.0")), root);

        Check("实例：首个包直接用加载器版本目录名",
            first.InstanceId == versionId && first.VersionId == versionId,
            $"{first.InstanceId} / {first.VersionId}");
        Check("实例：同加载器的第二个包排到 -2，版本目录仍共用同一个",
            second.InstanceId == versionId + "-2" && second.VersionId == versionId,
            $"{second.InstanceId} / {second.VersionId}");
        Check("实例：同一份包重复导入复用原实例（不会造出 -3）",
            repeat.InstanceId == versionId, repeat.InstanceId);
        Check("实例：实例目录落在 instances/<id>/.minecraft",
            first.InstanceDirectory == Path.Combine(root, "instances", versionId, ".minecraft")
            && second.InstanceDirectory == Path.Combine(root, "instances", versionId + "-2", ".minecraft"),
            second.InstanceDirectory);

        var metadataA = InstanceMetadata.TryRead(root, first.InstanceId);
        var metadataB = InstanceMetadata.TryRead(root, second.InstanceId);
        Check("实例：两个包的 instance.json 各归各，没被覆盖",
            metadataA is { Name: "整合包甲", PackVersion: "1.0.0", LoaderVersionId: versionId }
            && metadataB is { Name: "整合包乙", PackVersion: "2.0.0", LoaderVersionId: versionId },
            $"{metadataA?.Name} / {metadataB?.Name}");
        Check("实例：两个实例目录互不相同（各自独立的 mods / 配置）",
            !string.Equals(first.InstanceDirectory, second.InstanceDirectory, StringComparison.OrdinalIgnoreCase),
            $"{first.InstanceDirectory} vs {second.InstanceDirectory}");

        var instanceDirectories = Directory
            .GetDirectories(Path.Combine(root, "instances"))
            .Select(Path.GetFileName)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        Check("实例：磁盘上正好两个实例目录",
            instanceDirectories.Length == 2
            && instanceDirectories[0] == versionId && instanceDirectories[1] == versionId + "-2",
            string.Join(" | ", instanceDirectories));

        var loaderVersionDirectories = Directory
            .GetDirectories(Path.Combine(root, "versions"))
            .Select(Path.GetFileName)
            .ToArray();
        Check("实例：versions/ 里只有一个加载器版本目录（没有复制第二份）",
            loaderVersionDirectories.Length == 2
            && loaderVersionDirectories.Contains(versionId)
            && loaderVersionDirectories.Contains("1.21.4"),
            string.Join(" | ", loaderVersionDirectories));

        var index = InstanceRegistry.Load(root);
        Check("实例：index.json 落盘并登记了两个实例",
            File.Exists(Path.Combine(root, "instances", InstanceRegistry.FileName))
            && index.Find(versionId) is not null && index.Find(versionId + "-2") is not null,
            index.Instances.Count + " 条");
        Check("实例：索引里两条都指向同一个版本目录",
            index.Find(versionId + "-2")?.VersionId == versionId,
            index.Find(versionId + "-2")?.VersionId);
        Check("实例：ResolveVersionId 能把 -2 实例还原成版本目录",
            InstanceRegistry.ResolveVersionId(root, versionId + "-2") == versionId,
            InstanceRegistry.ResolveVersionId(root, versionId + "-2"));

        // 每次导入都要合一次 profile（第三个包是同包重复导入，也照装一遍加载器），3 次正好
        Check("实例：加载器 profile 每个包各合一次（3 次），没有重复放大",
            loaderMeta.ProfileCalls == 3, loaderMeta.ProfileCalls.ToString());
    }
    finally { TryDelete(root); }
}

{
    var legacy = NewTempTree("instanceregistry-legacy");
    try
    {
        // 老形态：实例目录名 == 版本目录名，instance.json 里没有 loaderVersionId
        void WriteVersion(string id)
        {
            var directory = Path.Combine(legacy, "versions", id);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, id + ".json"), $$"""{"id":"{{id}}"}""");
        }

        WriteVersion("1.20.1");
        WriteVersion("quilt-loader-0.20.0-1.20.1");
        new InstanceMetadata("通天之路", "1.3.5", "Modrinth", "Quilt 0.20.0", "1.20.1", "2026-01-01T00:00:00+08:00")
            .Write(legacy, "quilt-loader-0.20.0-1.20.1");

        var indexedPath = Path.Combine(legacy, "instances", InstanceRegistry.FileName);

        var before = InstanceRegistry.Load(legacy);
        Check("迁移：没有 index.json 时按磁盘推导（实例目录名就是版本目录）",
            before.Find("quilt-loader-0.20.0-1.20.1") is { VersionId: "quilt-loader-0.20.0-1.20.1", Name: "通天之路" },
            before.Instances.Count + " 条");
        Check("迁移：Load 只读不写盘", !File.Exists(indexedPath), indexedPath);

        var migrated = InstanceRegistry.LoadOrMigrate(legacy);
        Check("迁移：LoadOrMigrate 落盘 index.json 并标记 Migrated",
            migrated.Migrated && File.Exists(indexedPath),
            migrated.Migrated.ToString());
        Check("迁移：只有 versions/ 没有实例目录的版本也登记（界面以前就是这么列的）",
            migrated.Find("1.20.1") is { VersionId: "1.20.1" },
            string.Join(",", migrated.Instances.Select(e => e.Id)));

        var again = InstanceRegistry.LoadOrMigrate(legacy);
        Check("迁移：第二次加载不再迁移（幂等）",
            !again.Migrated && again.Instances.Count == migrated.Instances.Count,
            $"{again.Migrated} / {again.Instances.Count}");

        // 老实例被排到 -2、又没有元数据：靠后缀还原版本目录
        Directory.CreateDirectory(Path.Combine(legacy, "instances", "quilt-loader-0.20.0-1.20.1-2"));
        var withSuffix = InstanceRegistry.LoadOrMigrate(legacy);
        Check("迁移：无元数据的 -2 实例按后缀推导回版本目录",
            withSuffix.Find("quilt-loader-0.20.0-1.20.1-2")?.VersionId == "quilt-loader-0.20.0-1.20.1",
            withSuffix.Find("quilt-loader-0.20.0-1.20.1-2")?.VersionId);

        // 索引写坏了不该把实例列表搞空
        File.WriteAllText(indexedPath, "{ 这不是 json");
        var recovered = InstanceRegistry.Load(legacy);
        Check("迁移：index.json 损坏时按磁盘重建，实例不丢",
            recovered.Find("quilt-loader-0.20.0-1.20.1-2") is not null
            && recovered.Find("1.20.1") is not null,
            recovered.Instances.Count + " 条");

        // 旧实例的启动路径：ResolveVersionId 必须指回真正有 <id>.json 的版本目录
        Check("迁移：ResolveVersionId 对老实例返回自身（快路径）",
            InstanceRegistry.ResolveVersionId(legacy, "quilt-loader-0.20.0-1.20.1") == "quilt-loader-0.20.0-1.20.1");
        Check("迁移：ResolveVersionId 对无元数据的新实例靠索引/后缀还原",
            InstanceRegistry.ResolveVersionId(legacy, "quilt-loader-0.20.0-1.20.1-2") == "quilt-loader-0.20.0-1.20.1",
            InstanceRegistry.ResolveVersionId(legacy, "quilt-loader-0.20.0-1.20.1-2"));
    }
    finally { TryDelete(legacy); }
}

Console.WriteLine("== 28. 下载条目备用地址：镜像失败可按条切回官方 ==");
{
    var sink = new CapturingDownloader();
    var rewriter = new RewritingDownloader(sink, new PrefixRewriter());

    var rewritten = new DownloadEntry("https://official.test/libraries/a.jar", "libraries/a.jar", 10, "aa", DownloadKind.Library);
    var untouched = new DownloadEntry("https://elsewhere.test/b.jar", "libraries/b.jar", 20, null, DownloadKind.Library);
    var presetFallback = new DownloadEntry("https://official.test/c.jar", "libraries/c.jar", 30, null, DownloadKind.Library, "https://fallback.test/c.jar");

    await rewriter.DownloadAsync([rewritten, untouched, presetFallback], "root");

    Check("改写成镜像时把官方地址记进 FallbackUrl",
        sink.LastEntries[0].Url == "https://mirror.test/libraries/a.jar"
            && sink.LastEntries[0].FallbackUrl == "https://official.test/libraries/a.jar",
        $"Url={sink.LastEntries[0].Url} FallbackUrl={sink.LastEntries[0].FallbackUrl}");
    Check("没被改写的条目不设 FallbackUrl",
        sink.LastEntries[1].Url == "https://elsewhere.test/b.jar" && sink.LastEntries[1].FallbackUrl is null);
    Check("已有 FallbackUrl 的条目不被镜像覆盖",
        sink.LastEntries[2].Url == "https://mirror.test/c.jar"
            && sink.LastEntries[2].FallbackUrl == "https://fallback.test/c.jar");
    Check("DownloadEntry 默认 FallbackUrl 为 null（旧调用点零改动）",
        new DownloadEntry("u", "p", 1, null, DownloadKind.Asset).FallbackUrl is null);
}

Console.WriteLine();
await FirstRunChecks.RunAsync(Check);
await OptimizationChecks.RunAsync(Check);
await FollowupChecks.RunAsync(Check);
await ResourceBrowserChecks.RunAsync(Check);
await NetworkSettingsChecks.RunAsync(Check);
await UpdateSigningChecks.RunAsync(Check);

if (failures.Count == 0)
{
    Console.WriteLine($"[ok] 全部 {passes} 项检查通过");
    return 0;
}
Console.Error.WriteLine($"[FAIL] {failures.Count} 项失败：");
foreach (var f in failures) Console.Error.WriteLine("  - " + f);
return 1;

static string? FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "MinecraftLauncher.sln"))) return dir.FullName;
        dir = dir.Parent;
    }
    return null;
}

// 造一个"看起来合法"的正版账号：字段齐全但 token 是假的，账号簿只关心序列化，不关心 token 真伪
static MicrosoftAccount SampleAccount(string uuid, string name) =>
    new(uuid, name, $"mc-access-{name}", $"ms-refresh-{name}", DateTimeOffset.UtcNow.AddHours(23).ToUniversalTime());

// 账号簿用例各自一个空目录；沙箱对 %TEMP% 下再建子目录有限制，落到仓库 scratch 里更稳（与 --live 的做法一致）
static string NewTempDataDir(string tag)
{
    var baseDir = FindRepoRoot() is { } root ? Path.Combine(root, "scratch") : Path.GetTempPath();
    var dir = Path.Combine(baseDir, $"accounts-check-{tag}-{Guid.NewGuid().ToString("N")[..8]}");
    Directory.CreateDirectory(dir);
    return dir;
}

static void TryDelete(string dir)
{
    try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    catch { /* 清理失败不影响结论 */ }
}

// 12~14 组用的临时目录：%TEMP% 下随机子目录，测完整个删掉，不碰仓库里的任何文件。
// %TEMP% 被策略挡住时退回仓库 scratch（8/10/11 组走的就是那条路），免得测试环境一变就整组挂。
static string NewTempTree(string tag)
{
    var name = $"mc-checks-{tag}-{Guid.NewGuid().ToString("N")[..8]}";
    var temp = Path.Combine(Path.GetTempPath(), name);
    try
    {
        Directory.CreateDirectory(temp);
        return temp;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        var fallback = Path.Combine(FindRepoRoot() ?? Path.GetTempPath(), "scratch", name);
        Directory.CreateDirectory(fallback);
        return fallback;
    }
}

// 现造一个只含指定条目的 zip：改名 .mrpack 就是整合包（MRPack 与 zip 同一容器）
static void CreateZipFile(string path, IReadOnlyList<(string Name, string Content)> entries)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using var stream = File.Create(path);
    using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
    foreach (var (name, content) in entries)
    {
        using var entry = archive.CreateEntry(name).Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        entry.Write(bytes, 0, bytes.Length);
    }
}

// 组装一个真实的 ModpackInstaller：拿假下载器 + 假加载器安装器，元数据客户端一律"不该被调用"
static ModpackInstaller BuildModpackInstaller(IDownloader downloader, IForgeInstaller forge, INeoForgeInstaller neoForge)
{
    var gameInstaller = new GameInstaller(downloader, new StubVersionMeta());
    var loaderInstaller = new LoaderInstaller(new StubLoaderMeta(), new StubVersionMeta(), gameInstaller);
    return new ModpackInstaller(gameInstaller, loaderInstaller, forge, neoForge, downloader);
}

// 造一个和 Adoptium 包同构的最小 JRE zip：单个顶层目录 + bin/java.exe，用来验「剥一层」和 staging 清理
static byte[] BuildFakeJreZip(string topLevel)
{
    using var buffer = new MemoryStream();
    using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
    {
        using (var exe = zip.CreateEntry($"{topLevel}/bin/java.exe").Open())
            exe.Write("fake java binary"u8);
        using (var release = zip.CreateEntry($"{topLevel}/release").Open())
            release.Write("JAVA_VERSION=\"21.0.12.1\"\n"u8);
    }
    return buffer.ToArray();
}

// 2026-10-22 抓的真实 /v3/assets/latest/21 响应（字段删到只留必要项，值保持原样）
static string AdoptiumReal21Json() => """
[
  {
    "binary": {
      "package": {
        "link": "https://github.com/adoptium/temurin21-binaries/releases/download/jdk-21.0.12.1%2B1/OpenJDK21U-jre_x64_windows_hotspot_21.0.12.1_1.zip",
        "checksum": "d35f31e712f0fcf6ac5a093edc90204fbff22f720ba3950bd09d331d5e621636",
        "name": "OpenJDK21U-jre_x64_windows_hotspot_21.0.12.1_1.zip",
        "size": 48999141
      },
      "installer": { "link": "", "checksum": "", "name": "", "size": 0 }
    },
    "version": { "major": 21, "openjdk_version": "21.0.12.1+1-LTS" },
    "vendor": "eclipse"
  }
]
""";

static string AdoptiumFixtureJson(string link, string checksum, string name, long size, int major, string openjdkVersion) => $$"""
[
  {
    "binary": {
      "package": { "link": "{{link}}", "checksum": "{{checksum}}", "name": "{{name}}", "size": {{size}} }
    },
    "version": { "major": {{major}}, "openjdk_version": "{{openjdkVersion}}" },
    "vendor": "eclipse"
  }
]
""";

/// <summary>
/// 造一个最小 native jar：条目名可以带子目录（验证解压时会被拍平），内容无所谓——缓存只比对长度。
/// </summary>
static string WriteNativeJar(string directory, string fileName, params string[] entries)
{
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, fileName);
    using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
    foreach (var entry in entries)
    {
        var archiveEntry = zip.CreateEntry(entry);
        using var stream = archiveEntry.Open();
        var payload = Encoding.UTF8.GetBytes("fake-native:" + entry);
        stream.Write(payload, 0, payload.Length);
    }

    return path;
}

/// <summary>只回一份固定 JSON 的 HTTP 桩：Java 元数据检查不该走真实网络。</summary>
sealed class StubHttpHandler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
{
    public int Calls { get; private set; }

    public List<string> Urls { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        Urls.Add(request.RequestUri?.ToString() ?? "");
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(respond(request), Encoding.UTF8, "application/json"),
        };
        return Task.FromResult(response);
    }
}

/// <summary>假下载器：记调用次数，并把测试给的字节写到目标路径（用来喂坏包或合成好的 zip）。</summary>
sealed class FakeDownloader(Func<IReadOnlyList<DownloadEntry>, string, byte[]?> payload) : IDownloader
{
    public int Calls { get; private set; }

    public List<DownloadEntry> Requests { get; } = [];

    public Task<DownloadSummary> DownloadAsync(IReadOnlyList<DownloadEntry> entries, string rootDirectory,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        Calls++;
        Requests.AddRange(entries);

        var bytes = payload(entries, rootDirectory);
        if (bytes is not null)
        {
            foreach (var entry in entries)
            {
                var target = Path.Combine(rootDirectory, entry.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, bytes);
                progress?.Report(new DownloadProgress(bytes.Length, bytes.Length, 1, entries.Count, entry.RelativePath));
            }
        }

        return Task.FromResult(new DownloadSummary(entries.Count, entries.Count, 0, bytes?.Length ?? 0, TimeSpan.Zero));
    }
}

/// <summary>内存里的整合包源：元数据直接给，overrides 解压行为可注入（用来造中途失败）。</summary>
sealed class FakeModpackSource(
    ModrinthPack pack,
    Func<string, IProgress<ModpackImportProgress>?, CancellationToken, Task<int>>? extract = null) : IModpackSource
{
    public string SourcePath => "memory://fixture.mrpack";

    public ModrinthPack Pack { get; } = pack;

    public Task<int> ExtractOverridesAsync(
        string targetDirectory,
        IProgress<ModpackImportProgress>? progress = null,
        CancellationToken ct = default) =>
        extract is null ? Task.FromResult(0) : extract(targetDirectory, progress, ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>模拟真实 Forge installer：先把 versions/&lt;id&gt;/ 建一半，再在打补丁那步挂掉。</summary>
sealed class FailingForgeInstaller(string versionId) : IForgeInstaller
{
    public Task<string> InstallAsync(
        string minecraftVersion,
        string forgeVersion,
        string gameRoot,
        IProgress<ModpackImportProgress>? progress = null,
        CancellationToken ct = default)
    {
        var directory = Path.Combine(gameRoot, "versions", versionId);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, versionId + ".json"), "{\"id\":\"" + versionId + "\"}");

        throw new InvalidOperationException($"模拟 Forge installer 中途失败：{versionId}");
    }
}

sealed class ThrowingForgeInstaller : IForgeInstaller
{
    public Task<string> InstallAsync(
        string minecraftVersion,
        string forgeVersion,
        string gameRoot,
        IProgress<ModpackImportProgress>? progress = null,
        CancellationToken ct = default) =>
        throw new InvalidOperationException("这条用例里不该装 Forge");
}

sealed class ThrowingNeoForgeInstaller : INeoForgeInstaller
{
    public Task<string> InstallAsync(
        string minecraftVersion,
        string neoForgeVersion,
        string gameRoot,
        IProgress<ModpackImportProgress>? progress = null,
        CancellationToken ct = default) =>
        throw new InvalidOperationException("这条用例里不该装 NeoForge");
}

/// <summary>下载器整批失败：一个字节都不落盘，直接把批量异常抛出来。</summary>
sealed class FailingDownloader(Exception error) : IDownloader
{
    public int Calls { get; private set; }

    public Task<DownloadSummary> DownloadAsync(
        IReadOnlyList<DownloadEntry> entries,
        string rootDirectory,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        Calls++;
        return Task.FromException<DownloadSummary>(error);
    }
}

/// <summary>元数据客户端存根：整合包导入用例不该回源，一被调用就说明路径不对。</summary>
sealed class StubVersionMeta : IVersionMetaClient
{
    public Task<VersionManifest> GetManifestAsync(CancellationToken ct = default) =>
        throw new NotSupportedException("整合包导入用例不该回源版本清单");

    public Task<VersionJson> GetVersionAsync(string versionId, CancellationToken ct = default) =>
        throw new NotSupportedException("整合包导入用例不该回源版本 JSON");

    public Task<AssetIndex> GetAssetIndexAsync(AssetIndexRef reference, CancellationToken ct = default) =>
        throw new NotSupportedException("整合包导入用例不该回源 asset index");
}

sealed class StubLoaderMeta : ILoaderMetaClient
{
    public Task<IReadOnlyList<string>> GetLoaderVersionsAsync(LoaderKind kind, string gameVersion, CancellationToken ct = default) =>
        throw new NotSupportedException("整合包导入用例不该拉加载器版本列表");

    public Task<VersionJson> GetProfileAsync(LoaderKind kind, string gameVersion, string loaderVersion, CancellationToken ct = default) =>
        throw new NotSupportedException("整合包导入用例不该拉加载器 profile");
}

/// <summary>固定返回一份加载器 profile：验证"同一个加载器版本挂多个实例"时不必连真网络。</summary>
sealed class FixedLoaderMeta(string loaderVersion, string profileJson) : ILoaderMetaClient
{
    public int ProfileCalls { get; private set; }

    public Task<IReadOnlyList<string>> GetLoaderVersionsAsync(LoaderKind kind, string gameVersion, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>([loaderVersion]);

    public Task<VersionJson> GetProfileAsync(LoaderKind kind, string gameVersion, string loaderVersion, CancellationToken ct = default)
    {
        ProfileCalls++;
        var profile = JsonSerializer.Deserialize<VersionJson>(
            profileJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("profile 桩解析失败");
        return Task.FromResult(profile);
    }
}

/// <summary>把进度回调记下来，事后断言"哪个阶段报过什么"。</summary>
sealed class RecordingProgress<T> : IProgress<T>
{
    public List<T> Items { get; } = [];

    public void Report(T value) => Items.Add(value);
}

/// <summary>假定位器：探针结果由测试指定，从而不碰本机真正装着的 Java。</summary>
sealed class FakeJavaLocator(Func<string, JavaInstall?>? probe = null, IReadOnlyList<JavaInstall>? discovered = null)
    : IJavaLocator
{
    public Task<IReadOnlyList<JavaInstall>> DiscoverAsync(CancellationToken ct = default) =>
        Task.FromResult(discovered ?? Array.Empty<JavaInstall>());

    public Task<JavaInstall?> ProbeAsync(string javaExePath, CancellationToken ct = default) =>
        Task.FromResult(probe?.Invoke(javaExePath));
}

/// <summary>假加密器：2 字节头（0xFA + 密钥）+ 逐字节异或，用来验证「密文落盘 / 换密钥解不开」两条路径。</summary>
sealed class FakeProtector(byte key) : ISecretProtector
{
    public byte[] Protect(string plaintext)
    {
        var body = Encoding.UTF8.GetBytes(plaintext);
        var output = new byte[body.Length + 2];
        output[0] = 0xFA;
        output[1] = key;
        for (var i = 0; i < body.Length; i++) output[i + 2] = (byte)(body[i] ^ key);
        return output;
    }

    public string? TryUnprotect(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2 || payload[0] != 0xFA || payload[1] != key) return null;
        var body = new byte[payload.Length - 2];
        for (var i = 0; i < body.Length; i++) body[i] = (byte)(payload[i + 2] ^ key);
        return Encoding.UTF8.GetString(body);
    }

    public string Describe() => $"Fake(0x{key:X2})";
}

/// <summary>假 Modrinth 客户端：版本表由用例给定，顺便记录最大并发和收到的查询条件。</summary>
sealed class StubModrinthClient : IModrinthClient
{
    private readonly object _gate = new();
    private int _concurrent;

    public Dictionary<string, IReadOnlyList<ModrinthVersion>> Versions { get; init; } = new(StringComparer.Ordinal);

    /// <summary>每次查询的参数快照，形如 <c>slug|1.20.1|fabric</c>。</summary>
    public HashSet<string> Seen { get; } = new(StringComparer.Ordinal);

    /// <summary>每次查询先等这么久，用来制造并发与取消的窗口。</summary>
    public int DelayMs { get; init; }

    public int MaxConcurrent { get; private set; }

    public static ModrinthVersion Version(string projectId, string versionNumber, long size = 1024, string? url = null) =>
        new(
            Id: $"ver-{projectId}-{versionNumber}",
            ProjectId: projectId,
            Name: $"{projectId} {versionNumber}",
            VersionNumber: versionNumber,
            GameVersions: ["1.20.1"],
            Loaders: ["fabric"],
            DatePublished: DateTimeOffset.UtcNow,
            Files:
            [
                new ModrinthFile(
                    url ?? $"https://cdn.example/{projectId}-{versionNumber}.jar",
                    $"{projectId}-{versionNumber}.jar",
                    size,
                    true,
                    null),
            ],
            Dependencies: null);

    public Task<ModrinthSearchResponse> SearchAsync(
        string query,
        DownloadCategory category,
        string? gameVersion = null,
        string? loader = null,
        int limit = 20,
        int offset = 0,
        CancellationToken ct = default) => throw new NotSupportedException("更新检查不该走搜索");

    public async Task<IReadOnlyList<ModrinthVersion>> GetVersionsAsync(
        string projectIdOrSlug,
        string? gameVersion = null,
        string? loader = null,
        CancellationToken ct = default)
    {
        lock (Seen) Seen.Add($"{projectIdOrSlug}|{gameVersion}|{loader}");

        var concurrent = Interlocked.Increment(ref _concurrent);
        lock (_gate) { if (concurrent > MaxConcurrent) MaxConcurrent = concurrent; }

        try
        {
            if (DelayMs > 0) await Task.Delay(DelayMs, ct).ConfigureAwait(false);
            else await Task.Yield();

            return Versions.TryGetValue(projectIdOrSlug, out var versions) ? versions : [];
        }
        finally
        {
            Interlocked.Decrement(ref _concurrent);
        }
    }
}

/// <summary>假 CurseForge 客户端：按项目 ID 给文件表；没给的 ID 返回空（模拟 404）。</summary>
sealed class StubCurseForgeClient : ICurseForgeClient
{
    public bool IsConfigured { get; init; } = true;

    public Dictionary<int, IReadOnlyList<CfFile>> Files { get; init; } = [];

    public Task<CfSearchResponse> SearchAsync(
        string query,
        DownloadCategory category,
        string? gameVersion = null,
        string? loaderSlug = null,
        int pageSize = 20,
        int index = 0,
        CancellationToken ct = default) => throw new NotSupportedException("更新检查不该走搜索");

    public Task<CfMod?> GetModAsync(int modId, CancellationToken ct = default) => Task.FromResult<CfMod?>(null);

    public Task<IReadOnlyList<CfFile>> GetFilesAsync(
        int modId,
        string? gameVersion = null,
        string? loaderSlug = null,
        CancellationToken ct = default) =>
        Task.FromResult(Files.TryGetValue(modId, out var files) ? files : (IReadOnlyList<CfFile>)[]);

    public Task<string?> GetDownloadUrlAsync(int modId, int fileId, CancellationToken ct = default) =>
        Task.FromResult<string?>("https://cdn.example/cf-resolved.jar");

    public CfUrlRef? ParseUrl(string url) => null;
}

/// <summary>记录下载器收到的条目，用于验证镜像改写与 FallbackUrl 语义。</summary>
sealed class CapturingDownloader : IDownloader
{
    public IReadOnlyList<DownloadEntry> LastEntries { get; private set; } = [];

    public Task<DownloadSummary> DownloadAsync(
        IReadOnlyList<DownloadEntry> entries,
        string rootDirectory,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        LastEntries = entries;
        return Task.FromResult(new DownloadSummary(entries.Count, entries.Count, 0, 0, TimeSpan.Zero));
    }
}

/// <summary>把 official.test 前缀换成 mirror.test 的固定改写器（只给第 28 组用）。</summary>
sealed class PrefixRewriter : IUrlRewriter
{
    public string Rewrite(string url) =>
        url.StartsWith("https://official.test/", StringComparison.Ordinal)
            ? "https://mirror.test/" + url["https://official.test/".Length..]
            : url;
}
