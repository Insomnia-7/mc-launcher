using System.Diagnostics;
using System.Security.Cryptography;
using Launcher.Application.Experience;

namespace Launcher.Application.Update;

public sealed record PreparedSingleFileUpdate(UpdateManifest Manifest, UpdateFile File, string Staging, string Target, string Entry);
public sealed record LauncherUpdateResult(string State, string Version, string Message, DateTimeOffset At, string? Backup = null, int? RestartPid = null);

/// <summary>验证签名中的单个 EXE，再将受信载荷映射到用户当前使用的文件名。</summary>
public static class SingleFileUpdate
{
    public static void ValidateManifest(UpdateManifest manifest)
    {
        if (manifest.Files.Count != 1 || manifest.Files[0].Path != Path.GetFileName(manifest.Files[0].Path) ||
            !manifest.Files[0].Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            manifest.Entry is {} entry && entry != manifest.Files[0].Path)
            throw new UpdateManifestException("此桌面版本仅接受包含一个启动器 EXE 的更新包。");
    }
    public static async Task<PreparedSingleFileUpdate> PrepareAsync(string staging, string target, string entry, UpdateSignatureVerifier? verifier = null, CancellationToken ct = default)
    {
        if (entry != Path.GetFileName(entry) || !entry.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新目标文件名无效。");
        ExperienceFiles.RejectLinks(staging); ExperienceFiles.RejectLinks(target);
        var manifestPath = Path.Combine(staging, "update.json"); var signaturePath = manifestPath + ".sig";
        if (!File.Exists(manifestPath) || !File.Exists(signaturePath) || new FileInfo(manifestPath).Length > UpdateSignatureVerifier.MaxManifestBytes || new FileInfo(signaturePath).Length > UpdateSignatureVerifier.MaxSignatureBytes)
            throw new UpdateManifestException("更新清单或签名缺失、大小无效。");
        ExperienceFiles.RejectLinks(manifestPath); ExperienceFiles.RejectLinks(signaturePath);
        var manifest = (verifier ?? new()).Verify(await File.ReadAllBytesAsync(manifestPath, ct), await File.ReadAllBytesAsync(signaturePath, ct));
        ValidateManifest(manifest);
        var file = manifest.Files[0]; var source = ExperienceFiles.Child(staging, file.Path); var destination = ExperienceFiles.Child(target, entry);
        ExperienceFiles.RejectLinks(source); ExperienceFiles.RejectLinks(destination);
        var payload = File.Exists(source) ? source : destination;
        if (!File.Exists(payload) || new FileInfo(payload).Length != file.Size) throw new IOException("更新文件缺失或大小不符，旧版保持不变。");
        await using (var stream = File.OpenRead(payload))
            if (!Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("更新文件校验失败，旧版保持不变。");
        if (File.Exists(source) && !Path.GetFullPath(source).Equals(Path.GetFullPath(Path.Combine(staging, entry)), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            var renamed = ExperienceFiles.Child(staging, entry); ExperienceFiles.RejectLinks(renamed);
            File.Copy(source, renamed, true);
        }
        return new(manifest, file with { Path = entry }, staging, target, entry);
    }
    public static async Task<string> ApplyAsync(PreparedSingleFileUpdate prepared, CancellationToken ct = default)
    {
        var backup = Path.Combine(prepared.Target, ".backup", "fangyu-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        ExperienceFiles.RejectLinks(backup);
        await UpdateFileTransaction.ApplyAsync([prepared.File], prepared.Staging, prepared.Target, backup, ct: ct);
        return backup;
    }
    public static string ResultPath(string staging) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(staging))!, "launcher-update-result.json");
    public static void AcknowledgeStartup(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "update", "launcher-update-result.json");
        var result = ExperienceFiles.Read<LauncherUpdateResult>(path);
        if (result is { State: "restarting" } && result.RestartPid == Environment.ProcessId)
            ExperienceFiles.Write(path, result with { State = "completed", Message = "启动器已更新并成功启动。", At = DateTimeOffset.UtcNow });
    }
}

/// <summary>从启动器的临时副本执行，不加载主窗口，不依赖系统安装 .NET。</summary>
public static class LauncherUpdateRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        if (args.Length % 2 != 0) return 1;
        for (var i = 0; i < args.Length; i += 2)
            if (args[i] is not ("--staging" or "--target" or "--entry" or "--wait-pid") || !options.TryAdd(args[i], args[i + 1])) return 1;
        if (!options.TryGetValue("--staging", out var staging) || !options.TryGetValue("--target", out var target) || !options.TryGetValue("--entry", out var entry) || !options.TryGetValue("--wait-pid", out var pidText) || !int.TryParse(pidText, out var pid) || pid <= 0 || pid == Environment.ProcessId) return 1;
        var resultPath = SingleFileUpdate.ResultPath(staging); string version = ""; string? backup = null; Process? restarted = null;
        var parentExited = false; var applied = false;
        void Save(string state, string message, int? restartPid = null) => ExperienceFiles.Write(resultPath, new LauncherUpdateResult(state, version, message, DateTimeOffset.UtcNow, backup, restartPid));
        try
        {
            var prepared = await SingleFileUpdate.PrepareAsync(staging, target, entry); version = prepared.Manifest.Version;
            // 写权限探测发生在主程序退出前。受保护目录无法更新时，用户仍可继续使用旧版。
            var probe = Path.Combine(target, ".fangyu-write-" + Guid.NewGuid().ToString("N"));
            try { await File.WriteAllTextAsync(probe, ""); } finally { File.Delete(probe); }
            Save("ready", "更新已校验，等待启动器退出。");
            try
            {
                using var parent = Process.GetProcessById(pid);
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2)); await parent.WaitForExitAsync(timeout.Token);
            }
            catch (ArgumentException) { }
            parentExited = true;
            prepared = await SingleFileUpdate.PrepareAsync(staging, target, entry);
            backup = await SingleFileUpdate.ApplyAsync(prepared); applied = true;
            Save("restarting", "正在启动新版本。");
            restarted = Process.Start(new ProcessStartInfo(Path.Combine(target, entry)) { WorkingDirectory = target, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden }) ?? throw new IOException("无法启动新版本。");
            Save("restarting", "正在确认新版本窗口就绪。", restarted.Id);
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(40))
            {
                var state = ExperienceFiles.Read<LauncherUpdateResult>(resultPath);
                if (state is { State: "completed" } && state.RestartPid == restarted.Id) return 0;
                if (restarted.HasExited) throw new IOException("新版启动后提前退出。");
                await Task.Delay(150);
            }
            throw new IOException("新版未能在规定时间内显示窗口。");
        }
        catch (Exception ex)
        {
            if (restarted is not null && !restarted.HasExited) { restarted.Kill(); await restarted.WaitForExitAsync(); }
            var message = "更新未完成：" + ex.Message;
            if (applied && backup is not null)
            {
                try
                {
                    if (!File.Exists(Path.Combine(backup, entry))) throw new IOException("缺少旧版本备份，请从发布页重新下载。");
                    await UpdateFileTransaction.ApplyAsync([new(entry, "", "", 0)], backup, target, Path.Combine(backup, "failed-new-version"));
                    message += " 已恢复旧版。";
                }
                catch (Exception rollback) { message += " 恢复失败：" + rollback.Message + "；备份位置：" + backup; }
            }
            try { Save("failed", message); } catch (IOException) { }
            if (parentExited && File.Exists(Path.Combine(target, entry)))
                try { Process.Start(new ProcessStartInfo(Path.Combine(target, entry)) { WorkingDirectory = target, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden }); } catch (Exception) { }
            return 2;
        }
        finally { restarted?.Dispose(); }
    }
}
