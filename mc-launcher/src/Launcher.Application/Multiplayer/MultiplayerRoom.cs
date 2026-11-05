using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Launcher.Domain.Launch;

namespace Launcher.Application.Multiplayer;

public sealed record RoomInvitation(int Schema,string Name,string Network,string Secret,int GamePort,string Peer,
    string? GameVersion,string? Loader,DateTimeOffset Expires)
{
    public const string Prefix="fangyu-room://";
    public const string HostAddress="10.144.144.1";
    public static RoomInvitation Create(string name,int port,string peer,string? gameVersion,string? loader)
    {
        var invitation=new RoomInvitation(1,name.Trim(),"fangyu-"+Guid.NewGuid().ToString("N"),Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),port,peer.Trim(),gameVersion,loader,DateTimeOffset.UtcNow.AddHours(12));
        invitation.Validate();return invitation;
    }
    public string Encode()=>Prefix+Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this)).TrimEnd('=').Replace('+','-').Replace('/','_');
    public static RoomInvitation Decode(string? text)
    {
        try
        {
            text=text?.Trim();if(text is null||text.Length>4096||!text.StartsWith(Prefix,StringComparison.Ordinal))throw new InvalidDataException();
            var raw=text[Prefix.Length..].Replace('-','+').Replace('_','/');raw=raw.PadRight((raw.Length+3)/4*4,'=');
            var invitation=JsonSerializer.Deserialize<RoomInvitation>(Convert.FromBase64String(raw),new JsonSerializerOptions{MaxDepth=8})??throw new InvalidDataException();
            invitation.Validate();return invitation;
        }
        catch(Exception ex)when(ex is FormatException or JsonException or ArgumentException or NullReferenceException or InvalidDataException)
        {throw new InvalidDataException("邀请信息无效或已过期，请让房主重新创建房间。",ex);}
    }
    public void Validate()
    {
        if(Schema!=1||string.IsNullOrWhiteSpace(Name)||Name.Length>60||Name.Any(char.IsControl)||Network is not{Length:39}||!Network.StartsWith("fangyu-")||!Network[7..].All(Uri.IsHexDigit)||
            Secret is not{Length:64}||!Secret.All(Uri.IsHexDigit)||GamePort is <1 or >65535||Expires<=DateTimeOffset.UtcNow||Expires>DateTimeOffset.UtcNow.AddHours(13)||
            GameVersion?.Length>80||Loader?.Length>80)throw new InvalidDataException("房间信息无效或已过期。");
        ValidatePeer(Peer);
    }
    public static Uri ValidatePeer(string peer)
    {
        if(peer is null||peer.Length>300||peer.Any(char.IsWhiteSpace)||!Uri.TryCreate(peer,UriKind.Absolute,out var uri)||
            uri.Scheme is not("tcp" or "udp" or "ws" or "wss" or "quic")||uri.Port is <1 or >65535||uri.UserInfo.Length>0||uri.Query.Length>0||uri.Fragment.Length>0||uri.AbsolutePath is not("" or "/")||string.IsNullOrEmpty(uri.Host))
            throw new InvalidDataException("节点地址无效，请输入 tcp://主机:端口 等受支持地址。");
        return uri;
    }
}

public sealed record GameServerStatus(string Version,int Online,int Maximum,string Description,long ElapsedMilliseconds);
public interface IGameServerStatusClient
{Task<GameServerStatus> QueryAsync(ServerAddress address,CancellationToken ct=default);}
public interface ILanWorldDiscovery
{Task<IReadOnlyList<LanWorld>> DiscoverAsync(TimeSpan window,CancellationToken cancellationToken=default);}
public sealed record RoomSession(RoomInvitation Invitation,bool IsHost,ServerAddress Address);
public interface IRoomNetwork : IDisposable
{
    bool IsInstalled {get;}
    string DownloadDescription {get;}
    RoomSession? Session {get;}
    event Action? Changed;
    Task InstallAsync(IProgress<double>? progress=null,CancellationToken ct=default);
    Task<RoomSession> StartAsync(RoomInvitation invitation,bool host,CancellationToken ct=default);
    void Stop();
}
