using System.Net;

namespace Launcher.Domain.Launch;

public sealed record ServerAddress(string Host,int Port)
{
    public string Endpoint=>Host.Contains(':')?$"[{Host}]:{Port}":$"{Host}:{Port}";
    public static ServerAddress Parse(string? text)
    {
        text=text?.Trim();
        if(string.IsNullOrEmpty(text)||text.Length>300||text.Any(char.IsWhiteSpace)||text.IndexOfAny(['/', '\\', '"', '\'', '?', '#'])>=0)
            throw new ArgumentException("请输入游戏地址，例如 192.168.1.8:25565。");
        string host;var port=25565;
        if(text.StartsWith('['))
        {
            var end=text.IndexOf(']');if(end<2)throw new ArgumentException("IPv6 地址需要使用 [地址]:端口。");
            host=text[1..end];if(end+1<text.Length&&(!text[(end+1)..].StartsWith(':')||!int.TryParse(text[(end+2)..],out port)))throw new ArgumentException("游戏端口无效。");
        }
        else if(text.Count(c=>c==':')==1)
        {var colon=text.LastIndexOf(':');host=text[..colon];if(!int.TryParse(text[(colon+1)..],out port))throw new ArgumentException("游戏端口无效。");}
        else host=text;
        if(port is <1 or >65535||Uri.CheckHostName(host)==UriHostNameType.Unknown)throw new ArgumentException("游戏主机或端口无效。");
        return new(host,port);
    }
}
