namespace Launcher.Application.Mods;

/// <summary>
/// Mod 搜索结果在界面上的统一形态。Modrinth 用 slug、CurseForge 用数字 id，
/// 都塞进 <see cref="Id"/>，靠 <see cref="Source"/> 区分怎么用。
/// </summary>
public sealed record ModSearchItem(
    string Source,
    string Id,
    string Title,
    string Author,
    string DownloadsText,
    string Description)
{
    public string Badge => Source == "CurseForge" ? "CF" : "MR";
}
