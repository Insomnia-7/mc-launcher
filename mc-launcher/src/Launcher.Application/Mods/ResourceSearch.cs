using Launcher.Domain.Content;

namespace Launcher.Application.Mods;

public enum ResourceSort { Relevance, Downloads, Popularity, Updated, Newest }
public sealed record ResourceSearchRequest(string Query, DownloadCategory Category,
    string? GameVersion = null, string? Loader = null, string? Tag = null,
    ResourceSort Sort = ResourceSort.Popularity, int Limit = 20, int Offset = 0);
public sealed record ResourceTag(string Id, string Name, string Group = "")
{
    public override string ToString() => Group.Length == 0 ? Name : $"{Group} · {Name}";
}
