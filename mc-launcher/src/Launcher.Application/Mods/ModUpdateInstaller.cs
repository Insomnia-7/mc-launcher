using Launcher.Application.Abstractions;
using Launcher.Application.Update;
using Launcher.Domain.Content;

namespace Launcher.Application.Mods;

/// <summary>新主文件及缺失依赖全部暂存并校验后，再一次提交替换；提交失败恢复原文件集合。</summary>
public sealed class ModUpdateInstaller(ModInstallService modrinth, CurseForgeInstallService curseForge, IDownloader downloader)
{
    public async Task<ModInstallReport> UpdateAsync(string gameRoot, string instanceId, ModUpdateCandidate candidate,
        IProgress<ModInstallProgress>? progress = null, CancellationToken ct = default)
    {
        var project = candidate.ProjectId ?? throw new InvalidOperationException("该文件没有可确认的平台项目来源，请先重新检查更新。");
        var plan = candidate.Source switch
        {
            ModUpdateCandidate.SourceModrinth => await modrinth.PrepareAsync(gameRoot, instanceId, DownloadCategory.Mod, project, progress, ct, strict: true, versionId: candidate.VersionId).ConfigureAwait(false),
            ModUpdateCandidate.SourceCurseForge when int.TryParse(project, out var id) => await curseForge.PrepareAsync(gameRoot, instanceId, DownloadCategory.Mod, id, progress, ct, strict: true, versionId: candidate.VersionId).ConfigureAwait(false),
            _ => throw new InvalidOperationException("无法识别该模组的更新来源。"),
        };
        var oldPath = Install.LongPath.Combine(plan.TargetDirectory, candidate.FileName);
        if (!File.Exists(oldPath)) throw new FileNotFoundException("原模组已不存在，请刷新列表", candidate.FileName);
        if (candidate.CurrentSha1 is { Length: > 0 } oldHash && !string.Equals(await ModSourceStore.HashAsync(oldPath, ct).ConfigureAwait(false), oldHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("模组文件已改变，请重新检查更新。");
        var pending = new List<PlannedModFile> { plan.Files[0] };
        foreach (var file in plan.Files.Skip(1))
        {
            var existing = await ModInstallExecutor.FindExistingAsync(plan, file, DownloadCategory.Mod, ct).ConfigureAwait(false);
            if (existing is null) { pending.Add(file); continue; }
            if (!file.RequiresExactVersion) continue;
            var hash = await ModSourceStore.HashAsync(existing, ct).ConfigureAwait(false);
            var recorded = ModSourceStore.Load(plan.TargetDirectory).GetValueOrDefault(Path.GetFileName(existing));
            var matches = !string.IsNullOrWhiteSpace(file.Entry.Sha1)
                ? hash.Equals(file.Entry.Sha1, StringComparison.OrdinalIgnoreCase)
                : recorded is not null && recorded.VersionId == file.Origin.VersionId && recorded.Sha1.Equals(hash, StringComparison.OrdinalIgnoreCase);
            if (!matches) throw new InvalidOperationException($"已安装前置与指定版本不符，请先更新该前置：{Path.GetFileName(existing)}");
        }
        var files = pending.ToArray();
        if (candidate.FileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
            files[0] = files[0] with { Entry = files[0].Entry with { RelativePath = files[0].Entry.RelativePath + ".disabled" } };
        var stage = Path.Combine(gameRoot, "instances", instanceId, ".launcher-staging", Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(gameRoot, "instances", instanceId, ".launcher-backup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            progress?.Report(ModInstallProgress.Of("准备更新文件", 0, plan.Title));
            await downloader.DownloadAsync(files.Select(f => f.Entry).ToArray(), stage, new ProgressAdapter(progress), ct).ConfigureAwait(false);
            var origins = ModSourceStore.Load(plan.TargetDirectory);
            origins.Remove(candidate.FileName);
            foreach (var file in files)
            {
                var origin = await ModSourceStore.CaptureAsync(stage, file, required: true, ct).ConfigureAwait(false);
                origins[file.Entry.RelativePath] = origin!;
            }
            await ModSourceStore.SaveAsync(stage, origins, ct).ConfigureAwait(false);
            var replacements = files.Select(f => new UpdateFile(f.Entry.RelativePath, "", "", f.Entry.Size))
                .Append(new UpdateFile(ModSourceStore.FileName, "", "", 0)).ToArray();
            var removals = string.Equals(files[0].Entry.RelativePath, candidate.FileName, StringComparison.OrdinalIgnoreCase) ? Array.Empty<string>() : [candidate.FileName];
            await UpdateFileTransaction.ApplyAsync(replacements, stage, plan.TargetDirectory, backup, ct: ct, removeFiles: removals).ConfigureAwait(false);
            progress?.Report(ModInstallProgress.Of("更新完成", 100, plan.Title));
            return new(plan.Title, plan.Version, files[0].Entry.RelativePath, plan.RootSize, plan.TargetDirectory, files.Length - 1);
        }
        finally
        {
            try { Directory.Delete(stage, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
    private sealed class ProgressAdapter(IProgress<ModInstallProgress>? progress) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => progress?.Report(ModInstallProgress.Of("准备更新文件", value.Percent, value.CurrentFile));
    }
}
