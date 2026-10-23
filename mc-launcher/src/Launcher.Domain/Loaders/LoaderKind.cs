namespace Launcher.Domain.Loaders;

/// <summary>加载器种类。Vanilla 表示不装加载器。</summary>
public enum LoaderKind
{
    Vanilla,
    Fabric,
    Quilt,
    Forge,
    NeoForge,
}

public static class LoaderKindExtensions
{
    /// <summary>版本 id 里用的短名：1.21.4-fabric / 1.20.1-forge。</summary>
    public static string Slug(this LoaderKind kind) => kind switch
    {
        LoaderKind.Vanilla => "vanilla",
        LoaderKind.Fabric => "fabric",
        LoaderKind.Quilt => "quilt",
        LoaderKind.Forge => "forge",
        LoaderKind.NeoForge => "neoforge",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static LoaderKind Parse(string text) => text.Trim().ToLowerInvariant() switch
    {
        "fabric" => LoaderKind.Fabric,
        "quilt" => LoaderKind.Quilt,
        "forge" => LoaderKind.Forge,
        "neoforge" => LoaderKind.NeoForge,
        "vanilla" or "none" => LoaderKind.Vanilla,
        _ => throw new ArgumentException($"未知加载器：{text}", nameof(text)),
    };
}
