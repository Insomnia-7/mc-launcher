using System.Text.Json;
using System.Text.Json.Serialization;

namespace Launcher.Infrastructure;

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };
}

/// <summary>
/// 把官方地址改写到镜像（BMCLAPI）。只做「前缀替换 + 原样拼接」：
/// 不解析 URL、不重建 query，所以 sha1 路径、<c>?query</c>、片段都不会被弄坏。
/// 映射表按实测可用的 BMCLAPI 路径维护，新增源只往 <see cref="Map"/> 里加一行。
/// </summary>
public static class SourceRewriter
{
    private const string BmclapiHost = "https://bmclapi2.bangbang93.com";

    /// <summary>
    /// 官方前缀 → 镜像前缀。顺序按前缀长度倒序，避免短前缀先把长前缀吃掉
    /// （例如 maven.neoforged.net/releases 必须先于 maven.neoforged.net 命中）。
    /// </summary>
    private static readonly (string From, string To)[] Map =
        new (string From, string To)[]
        {
            // 元数据：版本清单 / 版本 json / asset index / 日志配置，全在这两个 host 上
            ("https://launchermeta.mojang.com",              BmclapiHost),
            ("https://piston-meta.mojang.com",               BmclapiHost),
            ("https://piston-data.mojang.com",               BmclapiHost),
            // 1.12 及更早版本的 client.jar / assets 老域名
            ("https://launcher.mojang.com",                  BmclapiHost),
            // assets/objects 故意不映射：BMCLAPI 的 /assets 只有 ~170 KB/s，
            // 官方 resources.download.minecraft.net 实测 3 MB/s 起。assets 是整包里最大的一块
            // （1.21.4 有 403 MB / 4039 个对象），走镜像等于把总耗时翻几倍。
            // 需要完全离线/镜像兜底时，HTTP 层已有「失败换另一条路」的 DualRouteHandler。
            // libraries + 各家 maven
            ("https://libraries.minecraft.net",              BmclapiHost + "/maven"),
            ("https://maven.minecraftforge.net",             BmclapiHost + "/maven"),
            ("https://maven.neoforged.net/releases",         BmclapiHost + "/maven"),
            ("https://maven.fabricmc.net",                   BmclapiHost + "/maven"),
            // maven.quiltmc.org 故意不映射：BMCLAPI 的 /maven 没有 quilt 的产物，
            // 改写过去只会拿到 404，直连反而更快（实测 200 / 0.3 秒）。
            // Fabric 的 meta API（BMCLAPI 只镜像了 fabric 的 meta，quilt 的没有）
            ("https://meta.fabricmc.net",                    BmclapiHost + "/fabric-meta"),
        }
        .OrderByDescending(rule => rule.From.Length)
        .ToArray();

    /// <summary>当前映射表（只读快照，给自检 / 文档用）。</summary>
    public static IReadOnlyList<(string From, string To)> Rules => Map;

    /// <summary>按需改写。useMirror 为 false 时原样返回，保证默认行为不变。</summary>
    public static string Apply(string url, bool useMirror)
    {
        if (!useMirror || string.IsNullOrEmpty(url)) return url;

        foreach (var (from, to) in Map)
        {
            if (!url.StartsWith(from, StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsUrlBoundary(url, from.Length)) continue;   // https://piston-meta.mojang.com.evil.tld 不算命中

            return string.Concat(to, url.AsSpan(from.Length));
        }

        return url;
    }

    /// <summary>前缀之后必须是串尾或路径/查询分隔符，防止前缀命中别的域名。</summary>
    private static bool IsUrlBoundary(string url, int index) =>
        index >= url.Length || url[index] is '/' or '?' or '#';
}
