namespace Launcher.Application.Mods;

using Launcher.Domain.Content;

/// <summary>
/// CurseForge 官方 API 客户端。没有 API Key 时所有调用都会抛出
/// <see cref="CurseForgeNotConfiguredException"/>，界面应据此提示用户去填 Key。
/// </summary>
public interface ICurseForgeClient
{
    Task<CfSearchResponse> SearchAsync(ResourceSearchRequest request, CancellationToken ct = default) =>
        SearchAsync(request.Query, request.Category, request.GameVersion, request.Loader, request.Limit, request.Offset, ct);
    Task<IReadOnlyList<ResourceTag>> GetCategoriesAsync(DownloadCategory category, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ResourceTag>>([]);

    /// <summary>是否已配置 API Key。false 时其它成员不可用。</summary>
    bool IsConfigured { get; }

    /// <summary>按资源类型搜索（classId 由 <paramref name="category"/> 决定）。</summary>
    Task<CfSearchResponse> SearchAsync(
        string query,
        DownloadCategory category,
        string? gameVersion = null,
        string? loaderSlug = null,
        int pageSize = 20,
        int index = 0,
        CancellationToken ct = default);

    Task<CfMod?> GetModAsync(int modId, CancellationToken ct = default);

    /// <summary>取某个 mod 的全部文件，按版本与加载器过滤后按时间倒序。</summary>
    Task<IReadOnlyList<CfFile>> GetFilesAsync(
        int modId,
        string? gameVersion = null,
        string? loaderSlug = null,
        CancellationToken ct = default);

    /// <summary>作者关闭第三方分发时用它换取临时下载地址。</summary>
    Task<string?> GetDownloadUrlAsync(int modId, int fileId, CancellationToken ct = default);

    /// <summary>从 CurseForge 网页链接里抠出 modId / fileId（支持 /mc-mods/&lt;slug&gt; 与 /files/&lt;id&gt;）。</summary>
    CfUrlRef? ParseUrl(string url);
}

/// <summary>从 CurseForge 网页链接解析出来的标识，两者都可能为 null。</summary>
public sealed record CfUrlRef(int? ModId, int? FileId);

public sealed class CurseForgeNotConfiguredException(string message) : Exception(message);

/// <summary>
/// 官方搜索端点被拒（真实情况：个人 Core Key 常常只能读 /v1/mods/&lt;id&gt; 与 /files，
/// 调 /v1/mods/search 一律 403）。界面应降级为「按项目 ID / 链接直读」。
/// </summary>
public sealed class CurseForgeSearchUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
