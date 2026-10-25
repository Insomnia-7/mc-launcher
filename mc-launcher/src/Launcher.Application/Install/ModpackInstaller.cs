using Launcher.Application.Abstractions;
using Launcher.Application.Instances;
using Launcher.Application.Mods;
using Launcher.Domain.Install;
using Launcher.Domain.Loaders;

namespace Launcher.Application.Install;

/// <summary>整合包导入结果。</summary>
public sealed record ModpackImportReport(
    string PackName,
    string PackVersionId,
    string MinecraftVersion,
    LoaderKind Loader,
    string? LoaderVersion,
    string InstanceId,
    string InstanceDirectory,
    int PlannedFiles,
    long PlannedBytes,
    int DownloadedFiles,
    int SkippedFiles,
    int OverrideFiles,
    IReadOnlyList<MissingModpackFile>? MissingFiles = null,
    string? LoaderVersionId = null)
{
    public double PlannedMegabytes => PlannedBytes / 1024.0 / 1024.0;

    /// <summary>
    /// 实例实际挂在哪个 <c>versions/</c> 目录上。和 <see cref="InstanceId"/> 分开是因为
    /// 同一个加载器版本可以被多个整合包各开一个实例（<c>…-2</c>、<c>…-3</c>），版本目录共用。
    /// 老逻辑下两者相同，所以缺省返回 <see cref="InstanceId"/>。
    /// </summary>
    public string VersionId => LoaderVersionId ?? InstanceId;

    /// <summary>待补全清单（永远非 null，省得调用方到处判空）。</summary>
    public IReadOnlyList<MissingModpackFile> Pending => MissingFiles ?? [];

    public int MissingCount => Pending.Count;

    /// <summary>文件是否齐了。部分成功也算导入完成，但界面要能把缺的列出来。</summary>
    public bool Complete => MissingCount == 0;

    /// <summary>一行摘要，给状态栏 / CLI 尾部提示用。</summary>
    public string? MissingSummary => Complete ? null : MissingModpackFile.Summarize(Pending, 3);
}

/// <summary>
/// 整合包导入编排：原版 → 加载器 → 清单文件 → overrides。
/// 只负责顺序和落盘位置，具体下载 / 安装细节都在各自组件里。
/// <para>
/// 三件事在这里收口，别的层不用管：
/// ① 失败回滚（<see cref="ImportTransaction"/>）：只删本次新建的 <c>versions/</c>、<c>instances/</c> 子目录；
/// ② 待补全清单：下载挂了也要告诉用户缺哪个文件、从哪下、多大；
/// ③ 长路径：实例目录与文件校验统一走 <see cref="LongPath"/>。
/// </para>
/// </summary>
public sealed class ModpackInstaller(
    GameInstaller gameInstaller,
    LoaderInstaller loaderInstaller,
    IForgeInstaller forgeInstaller,
    INeoForgeInstaller neoForgeInstaller,
    IDownloader downloader)
{
    public async Task<ModpackImportReport> ImportAsync(
        IModpackSource source,
        string gameRoot,
        IProgress<ModpackImportProgress>? progress = null,
        CancellationToken ct = default)
    {
        var pack = source.Pack;
        var minecraft = pack.MinecraftVersion
            ?? throw new InvalidOperationException($"整合包「{pack.Name}」没有声明 minecraft 依赖，无法确定游戏版本");

        // 回滚凭据：开跑前记下 versions/ 与 instances/ 里已经有什么。
        // 之后的失败一律只删「这段时间新出现的目录」，用户原来的版本与实例碰都不碰。
        var transaction = ImportTransaction.Begin(gameRoot);

        // 下载失败时填这里的清单，最后随异常 / 报告一起交出去
        var missing = new List<MissingModpackFile>();
        ModpackImportStaging? staging=null;

        try
        {
            ct.ThrowIfCancellationRequested();
            // ① 原版：已经装过就跳过（资产是全游戏共享的，不要重下 400 MB）
            var vanillaJson = Path.Combine(gameRoot, "versions", minecraft, minecraft + ".json");
            if (!File.Exists(vanillaJson))
            {
                var stage = $"安装原版 {minecraft}";
                progress?.Report(ModpackImportProgress.Of(stage, 0));
                await gameInstaller
                    .InstallAsync(minecraft, gameRoot, includeAssets: true, Wrap(progress, stage), ct)
                    .ConfigureAwait(false);
            }

            // ② 加载器
            var (loader, loaderVersion, instanceId) = await InstallLoaderAsync(minecraft, pack, gameRoot, progress, ct)
                .ConfigureAwait(false);

            // ③ 整合包清单文件：落到 instances/<id>/.minecraft/，和启动时用的游戏目录是同一个
            //
            // 这里要分清两个 id：加载器安装返回的是「版本目录 id」（versions/<id>/），以前直接当实例 id 用，
            // 于是同一个加载器版本导第二个整合包会把前一个实例的 instance.json 连同 mods 一起覆盖掉。
            // 现在版本目录归 loaderVersionId，实例目录单独分配：同一份包重复导入复用原实例，
            // 被别的包占用就排到 "<版本 id>-2"、"‑3"，versions/ 里仍然共用同一个加载器版本。
            var loaderVersionId = instanceId;
            var registry = InstanceRegistry.LoadOrMigrate(gameRoot);
            var targetInstanceId = registry.AllocateInstanceId(
                loaderVersionId, pack.Name, pack.VersionId, pack.SourceLabel);

            var instanceDirectory=Path.Combine(gameRoot,"instances",targetInstanceId,".minecraft");
            staging=new ModpackImportStaging(gameRoot,targetInstanceId);
            var stagedDirectory=staging.GameDirectory;

            // CurseForge 的 manifest 只给 projectID/fileID，要走 ResolveFilesAsync 换直链；
            // Modrinth 的默认实现直接返回 index 里的清单，等于什么都没变。
            var files = await source.ResolveFilesAsync(progress, ct).ConfigureAwait(false);

            var entries = files
                .Where(f => f.ClientRequired && f.PrimaryUrl is not null)
                .Select(f => new DownloadEntry(f.PrimaryUrl!, f.Path, f.FileSize, f.Sha1, DownloadKind.ModpackFile))
                .ToList();

            await staging.ReuseExistingAsync(entries,ct).ConfigureAwait(false);
            DownloadSummary? summary = null;
            if (entries.Count > 0)
            {
                var stage = "下载整合包文件";
                progress?.Report(ModpackImportProgress.Of(stage, 0,
                    $"{entries.Count} 个 / {entries.Sum(e => e.Size) / 1024.0 / 1024.0:F1} MB"));

                try
                {
                    summary = await downloader
                        .DownloadAsync(entries, stagedDirectory, Wrap(progress, stage), ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 下载器只回一句「N 个文件失败」，不回是哪些。落盘后自己核一遍才知道缺什么。
                    missing.AddRange(FindMissing(entries, stagedDirectory));
                    throw;
                }

                // 下载器说处理过不等于文件真在（磁盘满、写一半被杀、假实现）——成功路径也核一遍，
                // 部分成功同样要给用户一份可读的待补全清单。
                missing.AddRange(FindMissing(entries, stagedDirectory));
            }

            // ④ overrides：整包内嵌的配置、资源、地图等
            progress?.Report(ModpackImportProgress.Of("解压 overrides", 0));
            var overrideFiles = await source.ExtractOverridesAsync(stagedDirectory, progress, ct).ConfigureAwait(false);

            // 导入清单的实际文件保留来源；overrides 改写了内容时，不绑定不匹配的来源。
            var sourceLabel = pack.SourceLabel.ToLowerInvariant();
            foreach (var group in files.Where(f => f.ClientRequired && sourceLabel is "modrinth" or "curseforge").GroupBy(f => Path.GetDirectoryName(LongPath.Combine(stagedDirectory, f.Path))!))
            {
                staging.CopyOriginIndex(group.Key);
                await ModSourceStore.RecordAsync(group.Key, group.Select(f => new PlannedModFile(
                    new DownloadEntry(f.PrimaryUrl ?? "", Path.GetFileName(f.Path), f.FileSize, f.Sha1, DownloadKind.ModpackFile),
                    new ModOrigin(sourceLabel, f.SourceProjectId, f.SourceFileId, null, f.Sha1 ?? "", f.FileSize))), ct).ConfigureAwait(false);
            }

            // ⑤ 记下实例身份：界面显示「通天之路 1.3.5」而不是 1.20.1-forge-47.4.18
            var metadata = new InstanceMetadata(
                pack.Name,
                pack.VersionId,
                pack.SourceLabel,
                DescribeLoader(loader, loaderVersion),
                minecraft,
                DateTimeOffset.Now.ToString("o"),
                loaderVersionId);
            ct.ThrowIfCancellationRequested();
            metadata.Write(staging.Root,targetInstanceId);
            await Task.Run(()=>staging.Apply(ct),ct).ConfigureAwait(false);

            // 索引与 instance.json 同源：instance.json 是事实，index.json 只是"有哪些实例"的索引，
            // 丢了/写坏了下次 LoadOrMigrate 会按磁盘重建，界面不用再扫 versions/ 目录。
            registry.Register(targetInstanceId, loaderVersionId, metadata);
            staging.Complete();

            if (missing.Count > 0)
            {
                // 进度 Detail 会进界面状态栏，也会被 CLI 的进度行打出来
                progress?.Report(ModpackImportProgress.Of(
                    "待补全", 100, MissingModpackFile.Summarize(missing, 3)));
            }

            return new ModpackImportReport(
                pack.Name,
                pack.VersionId,
                minecraft,
                loader,
                loaderVersion,
                targetInstanceId,
                instanceDirectory,
                entries.Count,
                entries.Sum(e => e.Size),
                summary?.DownloadedFiles ?? 0,
                summary?.SkippedFiles ?? 0,
                overrideFiles,
                missing,
                loaderVersionId);
        }
        catch (OperationCanceledException canceled)
        {
            // 用户按了取消：同样是「半成品状态」，回滚掉，然后把取消原样抛回去（界面按取消处理）
            staging?.Rollback();
            staging?.Cleanup();
            transaction.Rollback();
            var transferPaths=canceled.Data["CleanupPath"] is string path?new[]{path}:Array.Empty<string>();
            var remaining=(staging?.CleanupFailures??[]).Concat(transaction.RemainingPaths).Concat(transferPaths).Distinct().ToArray();
            throw new ModpackImportCanceledException(remaining,ct);
        }
        catch (Exception ex)
        {
            if (missing.Count > 0)
            {
                progress?.Report(ModpackImportProgress.Of(
                    "待补全", 0, MissingModpackFile.Summarize(missing, 3)));
            }

            staging?.Rollback();
            var removed = transaction.Rollback();
            throw ModpackImportFailedException.From(pack.Name, ex, missing, removed);
        }
        finally {staging?.Dispose();}
    }

    /// <summary>
    /// 核对哪些文件没落全。大小为 0 的条目不校验长度（规范里 fileSize 缺省就是 0）。
    /// </summary>
    private static List<MissingModpackFile> FindMissing(IReadOnlyList<DownloadEntry> entries, string root)
    {
        var missing = new List<MissingModpackFile>();

        foreach (var entry in entries)
        {
            var target = LongPath.Combine(root, entry.RelativePath);
            var length = LongPath.FileLength(target);

            if (length is null)
            {
                missing.Add(new MissingModpackFile(entry.RelativePath, entry.Url, entry.Size, "未下载"));
            }
            else if (entry.Size > 0 && length != entry.Size)
            {
                missing.Add(new MissingModpackFile(entry.RelativePath, entry.Url, entry.Size, $"大小不符（{length} B）"));
            }
        }

        return missing;
    }

    private async Task<(LoaderKind Loader, string? Version, string InstanceId)> InstallLoaderAsync(
        string minecraft,
        Launcher.Domain.Modpacks.ModrinthPack pack,
        string gameRoot,
        IProgress<ModpackImportProgress>? progress,
        CancellationToken ct)
    {
        if (pack.ForgeVersion is { } forge)
        {
            var stage = $"安装 Forge {forge}";
            progress?.Report(ModpackImportProgress.Of(stage, 0, "首次安装需要跑官方 installer，约 1–3 分钟"));
            var versionId = await forgeInstaller
                .InstallAsync(minecraft, forge, gameRoot, progress, ct)
                .ConfigureAwait(false);
            return (LoaderKind.Forge, forge, versionId);
        }

        if (pack.NeoForgeVersion is { } neoForge)
        {
            var stage = $"安装 NeoForge {neoForge}";
            progress?.Report(ModpackImportProgress.Of(stage, 0,
                "首次安装要下 installer 和 ~70 个库，冷启动约 2 分钟"));
            var versionId = await neoForgeInstaller
                .InstallAsync(minecraft, neoForge, gameRoot, progress, ct)
                .ConfigureAwait(false);
            return (LoaderKind.NeoForge, neoForge, versionId);
        }

        if (pack.FabricVersion is { } fabric)
        {
            var stage = $"安装 Fabric {fabric}";
            progress?.Report(ModpackImportProgress.Of(stage, 0));
            var report = await loaderInstaller
                .InstallAsync(minecraft, LoaderKind.Fabric, gameRoot, fabric, Wrap(progress, stage), ct)
                .ConfigureAwait(false);
            return (LoaderKind.Fabric, fabric, report.VersionId);
        }

        if (pack.QuiltVersion is { } quilt)
        {
            var stage = $"安装 Quilt {quilt}";
            progress?.Report(ModpackImportProgress.Of(stage, 0));
            var report = await loaderInstaller
                .InstallAsync(minecraft, LoaderKind.Quilt, gameRoot, quilt, Wrap(progress, stage), ct)
                .ConfigureAwait(false);
            return (LoaderKind.Quilt, quilt, report.VersionId);
        }

        // 纯原版整合包（只有 mods/ 没有加载器依赖）
        return (LoaderKind.Vanilla, null, minecraft);
    }

    /// <summary>把下载进度翻译成整合包阶段进度，界面只认 ModpackImportProgress 一种。</summary>
    private static IProgress<DownloadProgress>? Wrap(IProgress<ModpackImportProgress>? outer, string stage) =>
        outer is null ? null : new DownloadProgressAdapter(outer, stage);

    /// <summary>加载器的人类可读名字，例如「Forge 47.4.18」；纯原版返回 null。</summary>
    private static string? DescribeLoader(LoaderKind loader, string? version) =>
        loader == LoaderKind.Vanilla
            ? null
            : version is null ? loader.ToString() : $"{loader} {version}";

    /// <summary>下载进度适配器：同一阶段内的百分比 + 当前文件。</summary>
    private sealed class DownloadProgressAdapter(IProgress<ModpackImportProgress> outer, string stage)
        : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) =>
            outer.Report(ModpackImportProgress.Of(
                stage,
                value.Percent,
                $"{value.CompletedBytes / 1024.0 / 1024.0:F1} / {value.TotalBytes / 1024.0 / 1024.0:F1} MB · {value.CurrentFile}"));
    }
}
