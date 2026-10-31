using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Launcher.Application.Abstractions;
using Launcher.Application.Experience;
using Launcher.Application.Instances;
using Launcher.Application.Mods;
using Launcher.Application.Update;
using Launcher.Domain.Install;

namespace Launcher.Application.Install;

public sealed record PackFileChange(string Path, string Kind, string Reason, string? Before, string? After, bool ApplyByDefault)
{
    public bool IsConflict => Kind == "冲突";
}
public sealed record PackUpdatePlan(string GameRoot, string InstanceId, string Stage, InstanceMetadata Before,
    string MinecraftVersion, string Name, string Version, string Fingerprint, string ArchiveSha256, ModpackBaseline Baseline,
    IReadOnlyList<PackFileChange> Changes, bool HasBaseline)
{
    public int ConflictCount => Changes.Count(c => c.IsConflict);
    public string Summary => $"{Before.PackVersion ?? "原版本"} → {Version} · 新增 {Changes.Count(c => c.Kind == "新增")} · 更新 {Changes.Count(c => c.Kind is "更新" or "合并")} · 移除 {Changes.Count(c => c.Kind == "移除")} · 冲突 {ConflictCount}";
}
public sealed record PackUpdateRecovery(string Id, string Name, DateTimeOffset CreatedAt, Dictionary<string, PackManagedFile?> Files, bool Committed = false);

/// <summary>Compare base/local/upstream before committing. Only pack-owned paths are removed; user data stays outside the transaction.</summary>
public sealed class ModpackUpdateService(GameInstaller gameInstaller, ModpackInstaller installer, IDownloader downloader)
{
    public async Task<PackUpdatePlan> PrepareAsync(IModpackSource source, string gameRoot, string instanceId,
        ModpackOrigin? origin = null, IProgress<ModpackImportProgress>? progress = null, CancellationToken ct = default)
    {
        gameRoot = System.IO.Path.GetFullPath(gameRoot);
        var instance = ExperienceFiles.Child(System.IO.Path.Combine(gameRoot, "instances"), instanceId);
        ExperienceFiles.RejectLinks(instance);
        var before = InstanceMetadata.TryRead(gameRoot, instanceId) ?? throw new InvalidOperationException("实例缺少整合包信息。");
        if (!before.IsModpack) throw new InvalidOperationException("请选择整合包实例。");
        var pack = source.Pack;
        var minecraft = pack.MinecraftVersion ?? throw new InvalidDataException("整合包未声明 Minecraft 版本。");
        var baseline = ModpackBaseline.Read(instance);
        if (baseline?.Origin is {} previous && origin is {} next &&
            (!previous.Source.Equals(next.Source, StringComparison.OrdinalIgnoreCase) || previous.ProjectId != next.ProjectId))
            throw new InvalidOperationException("所选版本属于另一个整合包项目。");
        var stage = System.IO.Path.Combine(gameRoot, ".cache", "pack-updates", Guid.NewGuid().ToString("N"));
        var game = System.IO.Path.Combine(stage, "new");
        Directory.CreateDirectory(game);
        try
        {
            progress?.Report(ModpackImportProgress.Of("准备整合包更新", 0, pack.Name));
            var files = await source.ResolveFilesAsync(progress, ct).ConfigureAwait(false);
            var selected = files.Where(f => f.ClientRequired && ModpackBaseline.CanManage(f.Path)).ToArray();
            var entries = selected.Select(f => new DownloadEntry(f.PrimaryUrl ?? throw new InvalidDataException("整合包缺少下载地址：" + f.Path),
                f.Path, f.FileSize, f.Sha1, DownloadKind.ModpackFile)).ToArray();
            foreach (var file in entries) ExperienceFiles.Child(game, file.RelativePath);
            // A verified local copy can be reused without touching the live instance.
            foreach (var file in selected)
            {
                ct.ThrowIfCancellationRequested();
                var original = ExperienceFiles.Child(System.IO.Path.Combine(instance, ".minecraft"), file.Path);
                if (!File.Exists(original) || file.Sha1 is null && file.Sha512 is null) continue;
                if (!await VerifyAsync(original, file.FileSize, file.Sha1, file.Sha512, ct).ConfigureAwait(false)) continue;
                var destination = ExperienceFiles.Child(game, file.Path);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
                File.Copy(original, destination);
            }
            await downloader.DownloadAsync(entries, game, new DownloadAdapter(progress), ct).ConfigureAwait(false);
            foreach (var file in selected)
                if (!await VerifyAsync(ExperienceFiles.Child(game, file.Path), file.FileSize, file.Sha1, file.Sha512, ct).ConfigureAwait(false))
                    throw new InvalidDataException("整合包文件校验失败：" + file.Path);
            await source.ExtractOverridesAsync(game, progress, ct).ConfigureAwait(false);
            var retainedOrigin = origin ?? (baseline?.Name == pack.Name && baseline.Source.Equals(pack.SourceLabel, StringComparison.OrdinalIgnoreCase) && baseline.Origin is {} known ? known with { ReleaseId = "" } : null);
            var incoming = await ModpackBaseline.CaptureAsync(game, pack.Name, pack.VersionId, pack.SourceLabel, retainedOrigin, ct).ConfigureAwait(false);
            var currentGame = System.IO.Path.Combine(instance, ".minecraft");
            var changes = new List<PackFileChange>();
            foreach (var path in incoming.Files.Keys.Concat(baseline?.Files.Keys.AsEnumerable() ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                if (!ModpackBaseline.CanManage(path)) continue;
                var old = baseline?.Files.GetValueOrDefault(path);
                var updated = incoming.Files.GetValueOrDefault(path);
                var localPath = ExperienceFiles.Child(currentGame, path);
                var local = await ModpackBaseline.HashAsync(localPath, ct).ConfigureAwait(false);
                var upstream = updated?.Sha256;
                if (local == upstream) { changes.Add(new(path, "不变", "与新版本一致", local, upstream, false)); continue; }
                if (local == old?.Sha256 && (old is not null || local is null))
                { changes.Add(new(path, upstream is null ? "移除" : local is null ? "新增" : "更新", "整合包内容变更", local, upstream, true)); continue; }
                if (old is not null && upstream == old.Sha256)
                { changes.Add(new(path, "保留", "保留你的修改或删除", local, upstream, false)); continue; }
                if (old?.Json is {} baseJson && updated?.Json is {} newJson && local is not null && new FileInfo(localPath).Length <= 524288)
                {
                    var merged = MergeJson(baseJson, await File.ReadAllTextAsync(localPath, ct).ConfigureAwait(false), newJson);
                    if (merged is not null)
                    {
                        var mergedPath = ExperienceFiles.Child(System.IO.Path.Combine(stage, "merged"), path);
                        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(mergedPath)!);
                        await File.WriteAllTextAsync(mergedPath, merged, ct).ConfigureAwait(false);
                        changes.Add(new(path, "合并", "已合并互不冲突的 JSON 设置", local, await ModpackBaseline.HashAsync(mergedPath, ct).ConfigureAwait(false), true));
                        continue;
                    }
                }
                changes.Add(new(path, "冲突", baseline is null ? "旧实例没有原始清单；默认保留本地文件" : "本地与整合包都已修改；默认保留本地", local, upstream, false));
            }
            // The archive is retained privately so apply can install the exact reviewed loader dependencies.
            var archive = System.IO.Path.Combine(stage, "source.pack");
            File.Copy(source.SourcePath, archive);
            var plan = new PackUpdatePlan(gameRoot, instanceId, stage, before, minecraft, pack.Name, pack.VersionId,
                await FingerprintAsync(instance, changes.Select(c => c.Path), ct).ConfigureAwait(false),
                await ModpackBaseline.HashAsync(archive, ct).ConfigureAwait(false) ?? throw new IOException("整合包文件已消失。"), incoming, changes, baseline is not null);
            ExperienceFiles.Write(System.IO.Path.Combine(stage, "plan.json"), plan);
            progress?.Report(ModpackImportProgress.Of("更新预览已准备", 100, plan.Summary));
            return plan;
        }
        catch { ExperienceFiles.DeleteTree(stage, System.IO.Path.Combine(gameRoot, ".cache", "pack-updates")); throw; }
    }

    public async Task ApplyAsync(PackUpdatePlan plan, IReadOnlyDictionary<string, bool> choices, IModpackSourceFactory factory,
        IProgress<ModpackImportProgress>? progress = null, CancellationToken ct = default)
    {
        ValidateStage(plan);
        var instance = ExperienceFiles.Child(System.IO.Path.Combine(plan.GameRoot, "instances"), plan.InstanceId);
        if (await FingerprintAsync(instance, plan.Changes.Select(c => c.Path), ct).ConfigureAwait(false) != plan.Fingerprint)
            throw new InvalidOperationException("实例文件已改变，请重新准备更新预览。");
        var selected = plan.Changes.Where(c => c.Kind != "不变" && choices.GetValueOrDefault(c.Path, c.ApplyByDefault)).ToArray();
        var commit = System.IO.Path.Combine(plan.Stage, "commit");
        Directory.CreateDirectory(commit);
        var replacements = new List<UpdateFile>();
        var removals = new List<string>();
        foreach (var change in selected)
        {
            ct.ThrowIfCancellationRequested();
            if (!ModpackBaseline.CanManage(change.Path)) throw new InvalidDataException("整合包更新包含受保护文件。");
            var relative = ".minecraft/" + change.Path;
            if (change.After is null) { removals.Add(relative); continue; }
            var from = ExperienceFiles.Child(System.IO.Path.Combine(plan.Stage, change.Kind == "合并" ? "merged" : "new"), change.Path);
            if (await ModpackBaseline.HashAsync(from, ct).ConfigureAwait(false) != change.After)
                throw new InvalidDataException("更新预览文件已改变：" + change.Path);
            var destination = ExperienceFiles.Child(commit, relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
            File.Copy(from, destination, true);
            replacements.Add(new(relative, "", change.After, new FileInfo(destination).Length));
        }
        var archive = System.IO.Path.Combine(plan.Stage, "source.pack");
        if (await ModpackBaseline.HashAsync(archive, ct).ConfigureAwait(false) != plan.ArchiveSha256)
            throw new InvalidDataException("整合包文件与预览不一致，请重新准备更新。");
        await using var source = factory.Open(archive);
        if (source.Pack.Name != plan.Name || source.Pack.VersionId != plan.Version || source.Pack.MinecraftVersion != plan.MinecraftVersion)
            throw new InvalidDataException("整合包来源与预览不一致。");
        // Verify the resulting enabled mod set before installing or replacing anything.
        var replacedMods = selected.Where(c => c.Path.StartsWith("mods/", StringComparison.Ordinal)).Select(c => c.Path).ToHashSet(StringComparer.Ordinal);
        var liveMods = System.IO.Path.Combine(instance, ".minecraft", "mods");
        var projected = (Directory.Exists(liveMods) ? Directory.EnumerateFiles(liveMods).Where(p => !replacedMods.Contains("mods/" + System.IO.Path.GetFileName(p))) : [])
            .Concat(Directory.Exists(System.IO.Path.Combine(commit, ".minecraft", "mods")) ? Directory.EnumerateFiles(System.IO.Path.Combine(commit, ".minecraft", "mods")) : []);
        var pack = source.Pack;
        var slug = pack.ForgeVersion is not null ? "forge" : pack.NeoForgeVersion is not null ? "neoforge" : pack.FabricVersion is not null ? "fabric" : pack.QuiltVersion is not null ? "quilt" : null;
        var compatibility = ModCompatibility.Inspect(projected, plan.MinecraftVersion, slug,
            pack.ForgeVersion ?? pack.NeoForgeVersion ?? pack.FabricVersion ?? pack.QuiltVersion, ct: ct);
        if (compatibility.Any(i => i.IsError)) throw new InvalidOperationException("更新后的模组需要先处理：" + string.Join("；", compatibility.Where(i => i.IsError).Take(4).Select(i => i.Title)));
        var vanillaJson = System.IO.Path.Combine(plan.GameRoot, "versions", plan.MinecraftVersion, plan.MinecraftVersion + ".json");
        if (!File.Exists(vanillaJson)) await gameInstaller.InstallAsync(plan.MinecraftVersion, plan.GameRoot, includeAssets: true, ct: ct).ConfigureAwait(false);
        var loader = await installer.InstallLoaderAsync(plan.MinecraftVersion, source.Pack, plan.GameRoot, progress, ct).ConfigureAwait(false);
        // Preparing shared dependencies may take time. Detect edits made while it was running.
        if (await FingerprintAsync(instance, plan.Changes.Select(c => c.Path), ct).ConfigureAwait(false) != plan.Fingerprint)
            throw new InvalidOperationException("准备依赖期间实例发生变化，请重新预览。");
        var metadata = plan.Before with { PackVersion = plan.Version, Name = plan.Name, Source = plan.Baseline.Source,
            MinecraftVersion = plan.MinecraftVersion, Loader = ModpackInstaller.DescribeLoader(loader.Loader, loader.Version), LoaderVersionId = loader.InstanceId };
        metadata.Write(System.IO.Path.Combine(commit, "metadata"), plan.InstanceId);
        File.Move(InstanceMetadata.PathFor(System.IO.Path.Combine(commit, "metadata"), plan.InstanceId), System.IO.Path.Combine(commit, InstanceMetadata.FileName), true);
        ExperienceFiles.Write(System.IO.Path.Combine(commit, ModpackBaseline.FileName), plan.Baseline);
        replacements.Add(new(InstanceMetadata.FileName, "", "", 0));
        replacements.Add(new(ModpackBaseline.FileName, "", "", 0));
        var recoveryId = Guid.NewGuid().ToString("N");
        var recoveryDirectory = System.IO.Path.Combine(instance, ".pack-backups", recoveryId);
        var originals = new Dictionary<string, PackManagedFile?>();
        foreach (var path in replacements.Select(f => f.Path).Concat(removals).Distinct(StringComparer.Ordinal))
        {
            var target = ExperienceFiles.Child(instance, path);
            var hash = await ModpackBaseline.HashAsync(target, ct).ConfigureAwait(false);
            originals[path] = hash is null ? null : new(hash, new FileInfo(target).Length);
        }
        var recovery = new PackUpdateRecovery(recoveryId, plan.Before.ToDisplayTitle(plan.InstanceId), DateTimeOffset.UtcNow, originals);
        ExperienceFiles.Write(System.IO.Path.Combine(recoveryDirectory, "recovery.json"), recovery);
        var marker = ".pack-backups/" + recoveryId + "/recovery.json";
        ExperienceFiles.Write(ExperienceFiles.Child(commit, marker), recovery with { Committed = true });
        replacements.Add(new(marker, "", "", 0));
        progress?.Report(ModpackImportProgress.Of("备份并应用整合包更新", 0, plan.Summary));
        await UpdateFileTransaction.ApplyAsync(replacements, commit, instance, System.IO.Path.Combine(recoveryDirectory, "files"),
            ct: ct, removeFiles: removals).ConfigureAwait(false);
        Launcher.Application.Operations.DurableExecution.Current?.MarkCommitted();
        try { InstanceRegistry.LoadOrMigrate(plan.GameRoot); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* instance.json remains authoritative. */ }
        progress?.Report(ModpackImportProgress.Of("整合包更新完成", 100, "已保留存档、截图和用户新增文件；可恢复到更新前。"));
    }

    public IReadOnlyList<PackUpdateRecovery> Recoveries(string gameRoot, string instanceId)
    {
        var root = ExperienceFiles.Child(System.IO.Path.Combine(gameRoot, "instances"), instanceId + "/.pack-backups");
        ExperienceFiles.RejectLinks(root);
        return Directory.Exists(root) ? Directory.EnumerateDirectories(root)
            .Select(d => ExperienceFiles.Read<PackUpdateRecovery>(System.IO.Path.Combine(d, "recovery.json")))
            .Where(r => r is { Committed: true } && Guid.TryParseExact(r.Id, "N", out _)).Cast<PackUpdateRecovery>().OrderByDescending(r => r.CreatedAt).ToArray() : [];
    }

    public async Task RestoreAsync(string gameRoot, string instanceId, string recoveryId, CancellationToken ct = default)
    {
        var recovery = Recoveries(gameRoot, instanceId).FirstOrDefault(r => r.Id == recoveryId) ?? throw new InvalidDataException("找不到更新恢复点。");
        var instance = ExperienceFiles.Child(System.IO.Path.Combine(gameRoot, "instances"), instanceId);
        var root = ExperienceFiles.Child(System.IO.Path.Combine(instance, ".pack-backups"), recovery.Id);
        var files = new List<UpdateFile>();
        foreach (var (path, info) in recovery.Files)
        {
            if (path != InstanceMetadata.FileName && path != ModpackBaseline.FileName && (!path.StartsWith(".minecraft/", StringComparison.Ordinal) || !ModpackBaseline.CanManage(path[11..])))
                throw new InvalidDataException("恢复点包含不允许的文件。");
            ExperienceFiles.RejectLinks(ExperienceFiles.Child(instance, path));
            if (info is null) continue;
            var backup = ExperienceFiles.Child(System.IO.Path.Combine(root, "files"), path);
            if (await ModpackBaseline.HashAsync(backup, ct).ConfigureAwait(false) != info.Sha256) throw new InvalidDataException("恢复点文件校验失败：" + path);
            files.Add(new(path, "", info.Sha256, info.Bytes));
        }
        var rollback = System.IO.Path.Combine(instance, ".pack-backups", "restore-" + Guid.NewGuid().ToString("N"));
        await UpdateFileTransaction.ApplyAsync(files, System.IO.Path.Combine(root, "files"), instance, rollback, ct: ct,
            removeFiles: recovery.Files.Where(f => f.Value is null).Select(f => f.Key).ToArray()).ConfigureAwait(false);
        Launcher.Application.Operations.DurableExecution.Current?.MarkCommitted();
        // Older recovery points remain available; the same point is not offered twice after restoring it.
        ExperienceFiles.Write(System.IO.Path.Combine(root, "recovery.json"), recovery with { Committed = false });
        InstanceRegistry.LoadOrMigrate(gameRoot);
    }

    public void Discard(PackUpdatePlan plan) { ValidateStage(plan); ExperienceFiles.DeleteTree(plan.Stage, System.IO.Path.Combine(plan.GameRoot, ".cache", "pack-updates")); }
    private static void ValidateStage(PackUpdatePlan plan)
    {
        var root = System.IO.Path.Combine(plan.GameRoot, ".cache", "pack-updates");
        if (!Guid.TryParseExact(System.IO.Path.GetFileName(plan.Stage), "N", out _) || ExperienceFiles.Child(root, System.IO.Path.GetFileName(plan.Stage)) != plan.Stage)
            throw new InvalidDataException("无效的更新暂存路径。");
        ExperienceFiles.RejectLinks(plan.Stage);
    }
    private static async Task<string> FingerprintAsync(string instance, IEnumerable<string> managedPaths, CancellationToken ct)
    {
        var paths = managedPaths.Select(p => ".minecraft/" + p).Append(InstanceMetadata.FileName).Append(ModpackBaseline.FileName).ToList();
        var mods = System.IO.Path.Combine(instance, ".minecraft", "mods");
        if (Directory.Exists(mods)) paths.AddRange(ExperienceFiles.Files(mods).Select(p => System.IO.Path.GetRelativePath(instance, p)));
        var output = new StringBuilder();
        foreach (var path in paths.Select(p => p.Replace('\\', '/')).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            output.Append(path).Append(':').Append(await ModpackBaseline.HashAsync(ExperienceFiles.Child(instance, path), ct).ConfigureAwait(false) ?? "missing").Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(output.ToString())));
    }
    private static async Task<bool> VerifyAsync(string file, long size, string? sha1, string? sha512, CancellationToken ct)
    {
        if (!File.Exists(file) || size > 0 && new FileInfo(file).Length != size) return false;
        foreach (var (hash, algorithm) in new[] { (sha1, HashAlgorithmName.SHA1), (sha512, HashAlgorithmName.SHA512) })
        {
            if (string.IsNullOrWhiteSpace(hash)) continue;
            await using var stream = File.OpenRead(file);
            using var digest = IncrementalHash.CreateHash(algorithm);
            var buffer = new byte[65536]; int read;
            while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0) digest.AppendData(buffer, 0, read);
            if (!Convert.ToHexStringLower(digest.GetHashAndReset()).Equals(hash, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }
    public static string? MergeJson(string baseline, string local, string upstream)
    {
        try
        {
            var a = JsonNode.Parse(baseline); var b = JsonNode.Parse(local); var c = JsonNode.Parse(upstream);
            return Merge(a, b, c, out var result) ? result?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null" : null;
        }
        catch (JsonException) { return null; }
    }
    private static bool Merge(JsonNode? a, JsonNode? b, JsonNode? c, out JsonNode? result)
    {
        if (JsonNode.DeepEquals(b, c)) { result = b?.DeepClone(); return true; }
        if (JsonNode.DeepEquals(a, b)) { result = c?.DeepClone(); return true; }
        if (JsonNode.DeepEquals(a, c)) { result = b?.DeepClone(); return true; }
        if (a is JsonObject ao && b is JsonObject bo && c is JsonObject co)
        {
            var merged = new JsonObject();
            foreach (var key in ao.Select(p => p.Key).Concat(bo.Select(p => p.Key)).Concat(co.Select(p => p.Key)).Distinct())
            {
                var hasA = ao.ContainsKey(key); var hasB = bo.ContainsKey(key); var hasC = co.ContainsKey(key);
                // Deletion and explicit null are distinct changes.
                if (hasB == hasC && JsonNode.DeepEquals(bo[key], co[key])) { if (hasB) merged[key] = bo[key]?.DeepClone(); continue; }
                if (hasA == hasB && JsonNode.DeepEquals(ao[key], bo[key])) { if (hasC) merged[key] = co[key]?.DeepClone(); continue; }
                if (hasA == hasC && JsonNode.DeepEquals(ao[key], co[key])) { if (hasB) merged[key] = bo[key]?.DeepClone(); continue; }
                if (hasA && hasB && hasC && Merge(ao[key], bo[key], co[key], out var child)) { merged[key] = child; continue; }
                result = null; return false;
            }
            result = merged; return true;
        }
        result = null; return false;
    }
    private sealed class DownloadAdapter(IProgress<ModpackImportProgress>? progress) : IProgress<DownloadProgress>
    { public void Report(DownloadProgress value) => progress?.Report(ModpackImportProgress.Of("准备更新文件", value.Percent, value.CurrentFile)); }
}
