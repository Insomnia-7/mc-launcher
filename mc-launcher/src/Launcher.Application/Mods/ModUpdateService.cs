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
        : $"{ModId} 查不到上游（离线 / 404 / 没有 API Key）";
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
/// 按 <see cref="InstalledModScanner"/> 的扫描结果查上游最新版：先 Modrinth（用 modId 当 project id / slug），
/// 404 或不可用时再用 CurseForge（modId 必须是数字项目 ID）。两边都不通只记为失败，绝不抛。
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
        var scan = InstalledModScanner.Scan(directory, ct);

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

        return new ModUpdateReport(results);

        async Task RunAsync(InstalledMod mod, int index)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                results[index] = await ResolveAsync(mod, environment, category, ct).ConfigureAwait(false);
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

    private async Task<ModUpdateCandidate> ResolveAsync(
        InstalledMod mod,
        InstanceEnvironment environment,
        DownloadCategory category,
        CancellationToken ct)
    {
        // 资源包 / 光影与加载器无关，按加载器过滤会一个都查不到
        var loader = category.UsesLoader() ? environment.LoaderSlug : null;

        if (string.IsNullOrWhiteSpace(mod.ModId))
        {
            return Unresolved(mod);
        }

        try
        {
            var versions = await modrinth
                .GetVersionsAsync(mod.ModId, environment.GameVersion, loader, ct)
                .ConfigureAwait(false);

            if (versions.Count > 0)
            {
                var latest = versions[0];
                var file = latest.PrimaryFile;
                return new ModUpdateCandidate(
                    mod.ModId,
                    mod.FileName,
                    mod.Version,
                    latest.VersionNumber,
                    IsNewer(latest.VersionNumber, mod.Version),
                    file?.Url,
                    file?.Size,
                    ModUpdateCandidate.SourceModrinth);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // 404 / 限流 / 网络抖动 / 包不是 mod：换下一个源
        }

        if (curseForge.IsConfigured
            && int.TryParse(mod.ModId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var projectId))
        {
            try
            {
                var files = await curseForge
                    .GetFilesAsync(projectId, environment.GameVersion, loader, ct)
                    .ConfigureAwait(false);

                var latest = files.FirstOrDefault(f => !string.IsNullOrWhiteSpace(f.DownloadUrl))
                             ?? files.FirstOrDefault();

                if (latest is not null)
                {
                    var url = latest.DownloadUrl;
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        url = await curseForge.GetDownloadUrlAsync(projectId, latest.Id, ct).ConfigureAwait(false);
                    }

                    // CF 的 displayName 常是「JourneyMap 5.9.7 for 1.20.1」，取第一个版本号样的 token 来比
                    var raw = string.IsNullOrWhiteSpace(latest.DisplayName) ? latest.FileName : latest.DisplayName;
                    var latestVersion = VersionToken(raw);

                    return new ModUpdateCandidate(
                        mod.ModId,
                        mod.FileName,
                        mod.Version,
                        latestVersion,
                        IsNewer(latestVersion, mod.Version),
                        url,
                        latest.FileLength,
                        ModUpdateCandidate.SourceCurseForge);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // 没有 API Key / 403 / 项目不存在：记为失败
            }
        }

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
