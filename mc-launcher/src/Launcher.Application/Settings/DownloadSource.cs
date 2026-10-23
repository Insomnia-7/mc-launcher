namespace Launcher.Application.Settings;

/// <summary>
/// 下载源。默认 <see cref="Official"/>：没配过 settings.json 的机器行为与加镜像之前完全一致。
/// </summary>
public enum DownloadSource
{
    Official = 0,
    Bmclapi = 1,
}

/// <summary>下载源在 settings.json / 命令行里的文本形式。只认白名单，写错一律落回官方源。</summary>
public static class DownloadSourceText
{
    public const string OfficialToken = "official";
    public const string BmclapiToken = "bmclapi";

    public static string ToToken(this DownloadSource source) =>
        source == DownloadSource.Bmclapi ? BmclapiToken : OfficialToken;

    /// <summary>解析 CLI / 配置里的写法。<c>bmcl</c>、<c>mirror</c> 是给老 <c>--mirror</c> 习惯留的别名。</summary>
    public static bool TryParse(string? text, out DownloadSource source)
    {
        source = DownloadSource.Official;
        if (string.IsNullOrWhiteSpace(text)) return false;

        switch (text.Trim().ToLowerInvariant())
        {
            case OfficialToken:
            case "mojang":
            case "vanilla":
                source = DownloadSource.Official;
                return true;
            case BmclapiToken:
            case "bmcl":
            case "mirror":
                source = DownloadSource.Bmclapi;
                return true;
            default:
                return false;
        }
    }

    /// <summary>宽松解析：认不出来时按官方源处理（配置损坏不该阻止安装）。</summary>
    public static DownloadSource ParseOrDefault(string? text) =>
        TryParse(text, out var source) ? source : DownloadSource.Official;
}
