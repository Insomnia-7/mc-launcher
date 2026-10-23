using Launcher.Application.Install;

namespace Launcher.Application.Update;

/// <summary>逐文件原子替换，失败时恢复已替换的文件；备份保留供诊断与手工恢复。</summary>
public static class UpdateFileTransaction
{
    public static async Task ApplyAsync(IReadOnlyList<UpdateFile> files, string staging, string targetRoot,
        string backupRoot, string? runningExecutable = null, Action<string>? log = null, CancellationToken ct = default, IReadOnlyList<string>? removeFiles = null)
    {
        var changed = new List<(string Target, string? Backup)>();
        try
        {
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var staged = Resolve(staging, file.Path);
                var target = Resolve(targetRoot, file.Path);
                if (!File.Exists(staged))
                {
                    if (!File.Exists(target)) throw new FileNotFoundException("缺少暂存和目标文件", file.Path);
                    log?.Invoke($"已是最新 {file.Path}");
                    continue; // 调用方已完成全量大小与哈希校验。
                }
                if (runningExecutable is not null && string.Equals(Path.GetFullPath(target),
                    Path.GetFullPath(runningExecutable), StringComparison.OrdinalIgnoreCase))
                {
                    log?.Invoke($"跳过自身 {file.Path}");
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string? backup = null;
                if (File.Exists(target))
                {
                    backup = Resolve(backupRoot, file.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(target, backup, overwrite: false);
                    log?.Invoke($"备份 {file.Path}");
                }
                await ReplaceAsync(staged, target, ct).ConfigureAwait(false);
                changed.Add((target, backup));
                log?.Invoke($"替换 {file.Path}");
            }
            foreach (var relative in removeFiles ?? [])
            {
                ct.ThrowIfCancellationRequested();
                var target = Resolve(targetRoot, relative);
                if (!File.Exists(target)) continue;
                var backup = Resolve(backupRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Move(target, backup);
                changed.Add((target, backup));
                log?.Invoke($"移除旧文件 {relative}");
            }
        }
        catch (Exception failure)
        {
            var rollbackFailures = new List<Exception>();
            foreach (var (target, backup) in changed.AsEnumerable().Reverse())
            {
                try
                {
                    if (backup is null) File.Delete(target);
                    else await ReplaceAsync(backup, target, CancellationToken.None).ConfigureAwait(false);
                    log?.Invoke($"回滚 {target}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    rollbackFailures.Add(ex);
                    log?.Invoke($"回滚失败 {target}：{ex.Message}");
                }
            }
            if (rollbackFailures.Count > 0)
                throw new AggregateException($"更新失败，{rollbackFailures.Count} 个文件未能恢复，请从备份恢复：{backupRoot}",
                    new[] { failure }.Concat(rollbackFailures));
            log?.Invoke("本次已替换的文件全部恢复。");
            throw;
        }
    }

    private static string Resolve(string root, string relative)
    {
        if (Path.IsPathRooted(relative)) throw new InvalidDataException("更新目标必须是相对路径。");
        return LongPath.Normalize(LongPath.Combine(Path.GetFullPath(root), relative));
    }

    private static async Task ReplaceAsync(string source, string target, CancellationToken ct)
    {
        var temporary = target + ".mcl-update-" + Guid.NewGuid().ToString("N");
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    File.Copy(source, temporary, overwrite: true);
                    ct.ThrowIfCancellationRequested();
                    File.Move(temporary, target, overwrite: true);
                    return;
                }
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 5)
                {
                    await Task.Delay(300 * attempt, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            try { File.Delete(temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
