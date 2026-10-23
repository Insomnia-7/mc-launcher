using Launcher.Domain.Launch;
using Launcher.Domain.Versions;

namespace Launcher.Application.Launch;

/// <summary>
/// 启动前的一次性准备：算好 classpath → 把 native jar 解压/复用到稳定目录 → 回填 <see cref="LaunchContext.NativesDirectory"/>。
/// </summary>
/// <param name="Context">补齐了 <c>NativesDirectory</c> 的启动上下文，直接喂给 <see cref="LaunchPlanBuilder.Build"/>。</param>
/// <param name="Natives">解压/复用结果，可拿去写日志（<see cref="NativesProvision.Summary"/>）。</param>
public sealed record LaunchPreparation(LaunchContext Context, NativesProvision Natives)
{
    /// <summary>给人看的一行结论。</summary>
    public string Summary() => Natives.Summary();
}

/// <summary>
/// 启动准备的入口。调用顺序固定为「先算 classpath、再定 natives、最后才建 plan」——
/// 因为 native jar 列表来自 classpath 计算，而 plan 里的 <c>-Djava.library.path</c> 又必须指向解压结果。
/// </summary>
public static class LaunchPreparer
{
    /// <summary>
    /// 计算 classpath、准备 natives 目录，返回可直接用于 <see cref="LaunchPlanBuilder.Build"/> 的上下文。
    /// </summary>
    /// <param name="version">已合并继承的版本描述。</param>
    /// <param name="platform">平台判定（决定挑哪个 natives classifier）。</param>
    /// <param name="launch">原始启动上下文；<c>NativesDirectory</c> 会被结果覆盖。</param>
    /// <param name="gameRoot">游戏根目录，natives 缓存落在 <c>&lt;gameRoot&gt;/natives/&lt;versionId&gt;/&lt;指纹&gt;/</c>。</param>
    /// <param name="ct">取消令牌。</param>
    public static async Task<LaunchPreparation> PrepareAsync(
        VersionJson version,
        PlatformContext platform,
        LaunchContext launch,
        string gameRoot,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRoot);

        var classPath = await Task.Run(
            () => ClassPathBuilder.Build(version, platform, launch.LibrariesRoot, launch.ClientJarPath),
            ct).ConfigureAwait(false);

        var natives = await NativesProvisioner
            .ProvisionAsync(gameRoot, version.Id, classPath.NativeJars, ct)
            .ConfigureAwait(false);

        return new LaunchPreparation(launch with { NativesDirectory = natives.Directory }, natives);
    }
}
