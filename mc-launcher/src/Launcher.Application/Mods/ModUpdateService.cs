using System.Globalization;
using System.Text.RegularExpressions;
using Launcher.Application.Update;
using Launcher.Domain.Content;

namespace Launcher.Application.Mods;

/// <summary>一个本地文件的更新检查结果。<see cref="Source"/> 为 <c>modrinth</c> / <c>curseforge</c> / <c>none</c>（两边都没查到）。</summary>
public sealed record ModUpdateCandidate(
    string ModId,
    string FileName,
    string? CurrentVersion,
    string? LatestVersion,
    bool HasUpdate,
    string? DownloadUrl,
    long? Size,
    string Source)
{
    public string? ProjectId { get; init; }
    public string? VersionId { get; init; }
    public string? CurrentSha1 { get; init; }
    public const string SourceModrinth = "modrinth";
    public const string SourceCurseForge = "curseforge";
    public const string SourceNone = "none";

    /// <summary>查到了上游（不管有没有新版本）。</summary>
    public bool Resolved => !string.Equals(Source, SourceNone, StringComparison.Ordinal);

    /// <summary>人话：「journeymap 5.9.5 → 5.9.7（modrinth）」。</summary>
    public string Describe() => Resolved
        ? HasUpdate
            ? $"{ModId} {CurrentVersion ?? "?"} → {LatestVersion}（{Source}）"
            : $"{ModId} 已是最新（{LatestVersion}，{Source}）"
        : $"{ModId} 无法确认更新（来源未匹配或网络不可用）";
}

/// <summary>一次更新检查的汇总。<see cref="Candidates"/> 与扫描结果一一对应，顺序稳定。</summary>
public sealed record ModUpdateReport(IReadOnlyList<ModUpdateCandidate> Candidates)
{
    /// <summary>有新版可升的个数。</summary>
    public int UpdateCount => Candidates.Count(c => c.HasUpdate);

    /// <summary>成功问到上游的个数。</summary>
    public int CheckedCount => Candidates.Count(c => c.Resolved);

    /// <summary>两个源都没问到的个数（含读不出 modId 的坏包）。</summary>
    public int FailedCount => Candidates.Count(c => !c.Resolved);

    public IReadOnlyList<ModUpdateCandidate> Updates => Candidates.Where(c => c.HasUpdate).ToList();
}

/// <summary>
/// 按 <see cref="InstalledModScanner"/> 的扫描结果查上游最新版：优先使用经哈希验证的来源记录，
/// 未记录的文件通过 Modrinth 文件哈希匹配。无法确认项目来源时只记为不可更新。
///
/// 并发上限 4，每个文件最多各查一次；<c>ct</c> 一路透传，取消时立刻中断。
/// </summary>
public sealed partial class ModUpdateService(IModrinthClient modrinth, ICurseForgeClient curseForge)
{
    /// <summary>同时在飞的请求数。上游对并发很敏感，4 是「快」和「不被限流」的折中。</summary>
    public const int MaxConcurrency = 4;

    private const string Stage = "检查更新";

    public async Task<ModUpdateReport> CheckAsync(
        string gameRoot,
        string instanceId,
        DownloadCategory category = DownloadCategory.Mod,
        IProgress<ModInstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        var subdirectory = category.InstanceSubdirectory() ?? "mods";
        var directory = Path.Combine(gameRoot, "instances", instanceId, ".minecraft", subdirectory);
        var environment = InstanceEnvironment.Resolve(gameRoot, instanceId);
        var scan = await Task.Run(() => InstalledModScanner.Scan(directory, ct), ct).ConfigureAwait(false);
        var origins = ModSourceStore.Load(directory);

        if (scan.Mods.Count == 0)
        {
            progress?.Report(ModInstallProgress.Of(Stage, 100, "没有可检查的文件"));
            return new ModUpdateReport([]);
        }

        progress?.Report(ModInstallProgress.Of(
            Stage, 0, $"{environment.Describe()} · {scan.Mods.Count} 个文件"));

        var results = new ModUpdateCandidate[scan.Mods.Count];
        var finished = 0;

        using var gate = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);

        var tasks = scan.Mods.Select((mod, index) => RunAsync(mod, index));
        await Task.WhenAll(tasks).ConfigureAwait(false);

        progress?.Report(ModInstallProgress.Of(Stage, 100, $"{scan.Mods.Count}/{scan.Mods.Count}"));
        return new ModUpdateReport(results);

        async Task RunAsync(InstalledMod mod, int index)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                results[index] = await ResolveAsync(mod, environment, category, origins.GetValueOrDefault(mod.FileName), ct).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }

            var done = Interlocked.Increment(ref finished);
            progress?.Report(ModInstallProgress.Of(
                Stage,
                done * 100d / scan.Mods.Count,
                $"{done}/{scan.Mods.Count} · {mod.FileName}"));
        }
    }

    private async Task<ModUpdateCandidate> ResolveAsync(InstalledMod mod, InstanceEnvironment environment,
        DownloadCategory category, ModOrigin? recorded, CancellationToken ct)
    {
        try
        {
            var hash = await ModSourceStore.HashAsync(mod.FullPath, ct).ConfigureAwait(false);
            var origin = recorded?.Sha1.Equals(hash, StringComparison.OrdinalIgnoreCase) == true ? recorded : null;
            if (origin?.ProjectId is null)
            {
                var matched = await modrinth.GetVersionByHashAsync(hash, ct).ConfigureAwait(false);
                if (matched is null) return Unresolved(mod);
                origin = new("modrinth", matched.ProjectId, matched.Id, matched.VersionNumber, hash, mod.Bytes);
            }
            var loader = category.UsesLoader() ? environment.LoaderSlug : null;
            var current = origin.VersionNumber ?? mod.Version;
            var displayId = mod.ModId ?? mod.FileName;
            if (origin.Source == ModUpdateCandidate.SourceModrinth)
            {
                var versions = await modrinth.GetVersionsAsync(origin.ProjectId!, environment.GameVersion, loader, ct).ConfigureAwait(false);
                if (versions.FirstOrDefault() is not { } latest || latest.PrimaryFile is not { } file) return Unresolved(mod);
                return new(displayId, mod.FileName, current, latest.VersionNumber, IsNewer(latest.VersionNumber, current), file.Url, file.Size, origin.Source)
                { ProjectId = origin.ProjectId, VersionId = latest.Id, CurrentSha1 = hash };
            }
            if (origin.Source == ModUpdateCandidate.SourceCurseForge && curseForge.IsConfigured && int.TryParse(origin.ProjectId, out var projectId))
            {
                var files = await curseForge.GetFilesAsync(projectId, environment.GameVersion, loader, ct).ConfigureAwait(false);
                var latest = files.FirstOrDefault(f => !string.IsNullOrWhiteSpace(f.DownloadUrl)) ?? files.FirstOrDefault();
                if (latest is null) return Unresolved(mod);
                var url = latest.DownloadUrl ?? await curseForge.GetDownloadUrlAsync(projectId, latest.Id, ct).ConfigureAwait(false);
                var version = VersionToken(string.IsNullOrWhiteSpace(latest.DisplayName) ? latest.FileName : latest.DisplayName);
                return new(displayId, mod.FileName, VersionToken(current), version, IsNewer(version, VersionToken(current)), url, latest.FileLength, origin.Source)
                { ProjectId = origin.ProjectId, VersionId = latest.Id.ToString(), CurrentSha1 = hash };
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or System.Text.Json.JsonException or InvalidOperationException or OperationCanceledException)
        { /* 来源失效、文件改变或网络错误：该文件不可确认更新。 */ }
        return Unresolved(mod);
    }

    private static ModUpdateCandidate Unresolved(InstalledMod mod) =>
        new(mod.ModId ?? string.Empty,
            mod.FileName,
            mod.Version,
            null,
            false,
            null,
            null,
            ModUpdateCandidate.SourceNone);

    /// <summary>本地版本读不出来时不判「有更新」——宁可不提示，也不要每次扫描都糊一堆假更新。</summary>
    private static bool IsNewer(string? latest, string? current) =>
        !string.IsNullOrWhiteSpace(current)
        && !string.IsNullOrWhiteSpace(latest)
        && UpdateVersion.IsNewer(latest!, current);

    /// <summary>从「JourneyMap 5.9.7 for 1.20.1」这类文本里抠出版本号；抠不出来就原样返回。</summary>
    private static string VersionToken(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var match = VersionTokenPattern().Match(text);
        return match.Success ? match.Value : text.Trim();
    }

    [GeneratedRegex("(?<![0-9A-Za-z.])[0-9]+(?:\\.[0-9]+){1,3}(?:[-+][0-9A-Za-z.\\-]+)?")]
    private static partial Regex VersionTokenPattern();
}
