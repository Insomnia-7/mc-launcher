namespace Launcher.Application.Abstractions;

/// <summary>URL 改写（官方 ↔ 镜像）。放接口是为了让 Application 不认识具体镜像规则。</summary>
public interface IUrlRewriter
{
    string Rewrite(string url);
}

public sealed class OfficialUrlRewriter : IUrlRewriter
{
    public static readonly OfficialUrlRewriter Instance = new();
    public string Rewrite(string url) => url;
}
