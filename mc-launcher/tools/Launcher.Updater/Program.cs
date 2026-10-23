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

    var manifestPath = Path.Combine(options.Staging, "update.json");
    if (!File.Exists(manifestPath))
    {
        Console.Error.WriteLine($"暂存目录里没有 update.json：{manifestPath}");
        return 2;
    }

    var manifest = UpdateManifestParser.Parse(await File.ReadAllTextAsync(manifestPath));
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

        if (file.Size > 0 && new FileInfo(staged).Length != file.Size)
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

    var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
    var backupDirectory = Path.Combine(options.Target, ".backup", stamp);
    Directory.CreateDirectory(backupDirectory);

    var log = new StringBuilder();
    log.AppendLine($"mclupd {stamp} → {manifest.Version}");

    foreach (var file in manifest.Files)
    {
        var staged = StagePath(options.Staging, file.Path);
        var target = TargetPath(options.Target, file.Path);

        if (!File.Exists(staged))
        {
            // 校验阶段已经确认目标本身符合清单：这个文件不需要动
            log.AppendLine($"已是最新 {file.Path}");
            continue;
        }

        // 更新器自己的 exe 正在运行，Windows 不允许覆盖它。
        // 清单里的 mclupd 与当前运行的是同一版本（CI 一起发布），跳过即可；
        // 真要换更新器本身，下次由新版启动器重新暂存后再走一遍。
        if (Environment.ProcessPath is { Length: > 0 } self &&
            string.Equals(Path.GetFullPath(target), Path.GetFullPath(self), StringComparison.OrdinalIgnoreCase))
        {
            log.AppendLine($"跳过自身 {file.Path}");
            continue;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        if (File.Exists(target))
        {
            var backup = Path.Combine(backupDirectory, file.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Copy(target, backup, overwrite: true);
            log.AppendLine($"备份 {file.Path}");
        }

        // 目标文件可能被占用：重试几轮，仍然失败就回滚已替换的并报错
        var copied = false;
        for (var attempt = 1; attempt <= 5 && !copied; attempt++)
        {
            try
            {
                File.Copy(staged, target, overwrite: true);
                copied = true;
                log.AppendLine($"替换 {file.Path}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 5)
            {
                await Task.Delay(300 * attempt);
            }
        }

        if (!copied)
        {
            Console.Error.WriteLine($"替换失败（文件被占用？）：{file.Path}");
            log.AppendLine($"失败 {file.Path}");
            await File.WriteAllTextAsync(Path.Combine(backupDirectory, "updater.log"), log.ToString());
            return 3;
        }
    }

    // 暂存的 update.json 不再需要，留在暂存目录里也行，顺手清掉避免下次误用
    File.Delete(manifestPath);

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
    if (file.Size > 0 && info.Length != file.Size)
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
