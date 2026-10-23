using System;
using System.Threading;
using System.Threading.Tasks;

namespace Launcher.Application.Update;

/// <summary>检查结果：有新版时 <see cref="Manifest"/> 非空，没有时 <see cref="Reason"/> 说明为什么。</summary>
/// <remarks>
/// <see cref="Failed"/> 区分「真的出错」和「本来就是最新」——两者都没有清单，
/// 但调用方（CLI、发布自检脚本）必须能把它们分开，否则签名验证失败会被当成「已是最新」。
/// </remarks>
public sealed record UpdateCheckResult(
    UpdateManifest? Manifest,
    string? Location = null,
    string? Reason = null,
    string? CurrentVersion = null,
    bool Failed = false)
{
    public bool HasUpdate => Manifest is not null;
}

/// <summary>暂存好的更新：目录里按 Path 摆好了文件，外加原始 update.json 与 update.json.sig 给 updater 再次验证。</summary>
public sealed record UpdateStaging(string Directory, UpdateManifest Manifest, int DownloadedFiles, int SkippedFiles);

public sealed record UpdateProgress(string Stage, double Percent, string? Detail);

/// <summary>
/// 启动器自更新。契约刻意做窄：只认「一份清单 + 一组文件」，不绑任何服务器实现，
/// 本地目录和 http 都能当来源（测试与自建 CDN 都靠这个）。
/// </summary>
public interface IUpdateClient
{
    /// <summary>
    /// 检查更新。<paramref name="location"/> 可以是 http(s) 地址、本地文件路径或目录；
    /// 传 null 时按环境变量 <c>MCLAUNCHER_UPDATE_URL</c> 找，都没有就返回「未配置」。
    /// </summary>
    Task<UpdateCheckResult> CheckAsync(string currentVersion, string? location = null, CancellationToken ct = default);

    /// <summary>把清单里的文件下到 <c>{dataDirectory}/update/{version}/</c>，已是最新内容的文件跳过。</summary>
    Task<UpdateStaging> StageAsync(
        UpdateManifest manifest,
        string? location,
        string targetDirectory,
        string dataDirectory,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken ct = default);
}
