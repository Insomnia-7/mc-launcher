using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Launcher.Application.Update;

// 独立的更新器：主程序先把新文件暂存好，再把自己交给这里。
// 单独一个 exe 是必须的 —— Windows 上正在运行的 exe / dll 不能被覆盖。

Console.OutputEncoding = Encoding.UTF8;

var options = UpdaterOptions.Parse(args);
if (options is null)
{
    Console.WriteLine("用法：mclupd --staging <暂存目录> --target <启动器目录> [--restart <exe>] [--wait-pid <pid>] [--entry <文件名>]");
    Console.WriteLine("  --staging    updater 读取新文件的目录（里面有 update.json）");
    Console.WriteLine("  --target     要被替换的启动器目录");
    Console.WriteLine("  --restart    更新完成后要启动的程序（相对 target 或绝对路径）");
    Console.WriteLine("  --wait-pid   等这个进程退出再动手（主程序自己传进来）");
    return 1;
}

try
{
    var manifestPath = Path.Combine(options.Staging, "update.json");
    var signaturePath = manifestPath + ".sig";
    if (!File.Exists(manifestPath) || !File.Exists(signaturePath))
        throw new UpdateManifestException("暂存更新缺少清单或发布者签名，已停止更新。");
    if (new FileInfo(manifestPath).Length > UpdateSignatureVerifier.MaxManifestBytes ||
        new FileInfo(signaturePath).Length > UpdateSignatureVerifier.MaxSignatureBytes)
        throw new UpdateManifestException("暂存清单或签名超过大小限制。");
    var manifest = new UpdateSignatureVerifier().Verify(await File.ReadAllBytesAsync(manifestPath), await File.ReadAllBytesAsync(signaturePath));

    if (options.WaitProcessId is { } pid && pid > 0)
    {
        Console.WriteLine($"等待进程 {pid} 退出…");
        try
        {
            using var process = Process.GetProcessById(pid);
            await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromMinutes(3)).Token);
        }
        catch (ArgumentException)
        {
            // 已经退了
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // PID 0 / 系统进程 / 没权限打开句柄：不该因此把更新拦下来，直接继续
            Console.WriteLine($"打不开进程 {pid}（{ex.Message}），按已退出处理");
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("等主程序退出超时，放弃更新");
            return 4;
        }
    }

    Console.WriteLine($"准备更新到 {manifest.Version}（{manifest.Files.Count} 个文件）");

    // 先全量校验，一个不对就整体不动 —— 半套文件比旧版更糟
    foreach (var file in manifest.Files)
    {
        var staged = StagePath(options.Staging, file.Path);
        if (!File.Exists(staged))
        {
            // 暂存里没有是正常的：下载器会跳过「目标文件已经符合清单」的条目
            // （版本间大量 dll 完全没变），那种文件本来就不需要替换。
            if (await TargetMatchesAsync(options.Target, file))
            {
                continue;
            }

            Console.Error.WriteLine($"缺少暂存文件：{file.Path}");
            return 2;
        }

        if (new FileInfo(staged).Length != file.Size)
        {
            Console.Error.WriteLine($"文件大小不对：{file.Path}");
            return 2;
        }

        if (!string.IsNullOrWhiteSpace(file.Sha256))
        {
            var actual = await Sha256Async(staged);
            if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"文件校验不通过：{file.Path}");
                return 2;
            }
        }
    }

    var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..8];
    var backupDirectory = Path.Combine(options.Target, ".backup", stamp);
    Directory.CreateDirectory(backupDirectory);

    var log = new StringBuilder();
    log.AppendLine($"mclupd {stamp} → {manifest.Version}");

    try
    {
        await UpdateFileTransaction.ApplyAsync(manifest.Files, options.Staging, options.Target, backupDirectory,
            Environment.ProcessPath, message => log.AppendLine(message));
    }
    catch (Exception ex)
    {
        log.AppendLine("更新失败：" + ex.Message);
        await File.WriteAllTextAsync(Path.Combine(backupDirectory, "updater.log"), log.ToString());
        Console.Error.WriteLine("更新失败：" + ex.Message);
        return 3;
    }

    // 暂存的 update.json 不再需要，留在暂存目录里也行，顺手清掉避免下次误用
    File.Delete(manifestPath);
    File.Delete(signaturePath);

    log.AppendLine("完成");
    await File.WriteAllTextAsync(Path.Combine(backupDirectory, "updater.log"), log.ToString());

    Console.WriteLine($"更新完成，备份在 {backupDirectory}");

    if (options.Restart is { Length: > 0 } restart)
    {
        var exe = Path.IsPathRooted(restart) ? restart : Path.Combine(options.Target, restart);
        if (File.Exists(exe))
        {
            Console.WriteLine($"重启 {exe}");
            Process.Start(new ProcessStartInfo(exe)
            {
                WorkingDirectory = options.Target,
                UseShellExecute = true,
            });
        }
        else
        {
            Console.Error.WriteLine($"找不到要重启的程序：{exe}");
        }
    }

    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("更新失败：" + ex.Message);
    return 1;
}

static string StagePath(string root, string relative) =>
    Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

static string TargetPath(string root, string relative) =>
    Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

static async Task<string> Sha256Async(string path)
{
    await using var stream = File.OpenRead(path);
    using var sha = SHA256.Create();
    return Convert.ToHexStringLower(await sha.ComputeHashAsync(stream));
}

// 目标文件是否已经是清单要求的那个版本（下载器按这个判定跳过下载）
static async Task<bool> TargetMatchesAsync(string targetRoot, UpdateFile file)
{
    var path = TargetPath(targetRoot, file.Path);
    if (!File.Exists(path))
    {
        return false;
    }

    var info = new FileInfo(path);
    if (info.Length != file.Size)
    {
        return false;
    }

    if (string.IsNullOrWhiteSpace(file.Sha256))
    {
        return true;
    }

    var actual = await Sha256Async(path);
    return string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase);
}

internal sealed record UpdaterOptions(
    string Staging,
    string Target,
    string? Restart,
    int? WaitProcessId)
{
    public static UpdaterOptions? Parse(string[] args)
    {
        string? staging = null;
        string? target = null;
        string? restart = null;
        int? waitPid = null;

        for (var index = 0; index + 1 < args.Length; index += 2)
        {
            var key = args[index];
            var value = args[index + 1];

            switch (key)
            {
                case "--staging": staging = value; break;
                case "--target": target = value; break;
                case "--restart": restart = value; break;
                case "--wait-pid": waitPid = int.TryParse(value, out var pid) ? pid : null; break;
                default: return null;
            }
        }

        if (string.IsNullOrWhiteSpace(staging) || string.IsNullOrWhiteSpace(target))
            return null;

        return new UpdaterOptions(
            Path.GetFullPath(staging),
            Path.GetFullPath(target),
            restart,
            waitPid);
    }
}
