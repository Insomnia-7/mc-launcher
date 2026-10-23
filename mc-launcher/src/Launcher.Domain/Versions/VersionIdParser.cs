namespace Launcher.Domain.Versions;

/// <summary>
/// 从版本目录名反推「加载器 + 加载器版本 + 游戏本体版本」，只给界面显示用。
/// 目录名是 maven 风格的行话，直接摆到界面上很难看：
/// <c>fabric-loader-0.20.1-1.21.4</c>、<c>1.20.1-forge-47.4.18</c>、<c>neoforge-21.1.9</c>。
/// 猜不出来就当成原版处理，绝不能因为解析失败把列表搞挂。
/// </summary>
public static class VersionIdParser
{
    public sealed record Parsed(string? Loader, string? LoaderVersion, string GameVersion)
    {
        public bool IsLoader => Loader is not null;

        /// <summary>标题：加载器实例给「Fabric 0.20.1」，原版给「1.21.4」。</summary>
        public string Title(string fallback) =>
            IsLoader ? $"{Loader} {LoaderVersion}" : fallback;
    }

    private const StringComparison Ci = StringComparison.OrdinalIgnoreCase;

    public static Parsed Parse(string id, string? inheritsFrom = null)
    {
        // 有 inheritsFrom 时那是权威的游戏本体版本，比从 id 里切更靠谱
        var parent = string.IsNullOrWhiteSpace(inheritsFrom) ? null : inheritsFrom;

        if (TrySplitPrefixed(id, "fabric-loader-", out var fabricVersion, out var fabricGame))
            return new Parsed("Fabric", fabricVersion, parent ?? fabricGame);

        if (TrySplitPrefixed(id, "quilt-loader-", out var quiltVersion, out var quiltGame))
            return new Parsed("Quilt", quiltVersion, parent ?? quiltGame);

        // 1.20.1-forge-47.4.18
        if (IndexOfSegment(id, "-forge-") is { } forgeIndex and > 0)
            return new Parsed("Forge", id[(forgeIndex + "-forge-".Length)..], id[..forgeIndex]);

        // neoforge-21.1.9 / 1.21.1-neoforge-21.1.9
        if (TrySplitPrefixed(id, "neoforge-", out var neoVersion, out _))
            return new Parsed("NeoForge", neoVersion, parent ?? neoVersion);

        if (IndexOfSegment(id, "-neoforge-") is { } neoIndex and > 0)
            return new Parsed("NeoForge", id[(neoIndex + "-neoforge-".Length)..], id[..neoIndex]);

        // 1.20.1-OptiFine_HD_U_I6 之类
        if (IndexOfSegment(id, "-optifine") is { } ofIndex and > 0)
            return new Parsed("OptiFine", id[(ofIndex + 1)..], id[..ofIndex]);

        // 没有加载器：原版
        return new Parsed(null, null, parent ?? id);
    }

    /// <summary>把 <c>fabric-loader-0.20.1-1.21.4</c> 切成 <c>0.20.1</c> 和 <c>1.21.4</c>。</summary>
    private static bool TrySplitPrefixed(string id, string prefix, out string version, out string game)
    {
        version = string.Empty;
        game = string.Empty;
        if (!id.StartsWith(prefix, Ci)) return false;

        var rest = id[prefix.Length..];
        var dash = rest.IndexOf('-');
        if (dash <= 0) return false;

        version = rest[..dash];
        game = rest[(dash + 1)..];
        return true;
    }

    private static int? IndexOfSegment(string id, string segment)
    {
        var index = id.IndexOf(segment, Ci);
        return index < 0 ? null : index;
    }
}
