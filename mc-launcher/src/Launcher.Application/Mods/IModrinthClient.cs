using Launcher.Domain.Content;

namespace Launcher.Application.Mods;

public interface IModrinthClient
{
    Task<ModrinthSearchResponse> SearchAsync(ResourceSearchRequest request, CancellationToken ct = default) =>
        SearchAsync(request.Query, request.Category, request.GameVersion, request.Loader, request.Limit, request.Offset, ct);
    Task<IReadOnlyList<ResourceTag>> GetCategoriesAsync(DownloadCategory category, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ResourceTag>>([]);

    /// <summary>按实际文件 SHA-1 匹配所属版本；未收录返回 null。</summary>
    Task<ModrinthVersion?> GetVersionByHashAsync(string sha1, CancellationToken ct = default) => Task.FromResult<ModrinthVersion?>(null);

    /// <summary>按资源类型搜索。<paramref name="category"/> 决定 project_type，原版版本不走这里。</summary>
    Task<ModrinthSearchResponse> SearchAsync(
        string query,
        DownloadCategory category,
        string? gameVersion = null,
        string? loader = null,
        int limit = 20,
        int offset = 0,
        CancellationToken ct = default);

    /// <summary>
    /// 取某个项目的版本列表（按发布时间倒序）。
    /// 整合包不要传 gameVersion —— 它自带 MC 版本，按实例版本过滤会全被筛掉。
    /// </summary>
    Task<IReadOnlyList<ModrinthVersion>> GetVersionsAsync(
        string projectIdOrSlug,
        string? gameVersion = null,
        string? loader = null,
        CancellationToken ct = default);
}
