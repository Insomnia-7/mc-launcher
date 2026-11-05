using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Launcher.Application.Multiplayer;
using Launcher.Domain.Launch;

namespace Launcher.Infrastructure.Multiplayer;

public sealed class GameServerStatusClient : IGameServerStatusClient
{
    public async Task<GameServerStatus> QueryAsync(ServerAddress address,CancellationToken ct=default)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(4));
        var clock=Stopwatch.StartNew();using var client=new TcpClient();
        await client.ConnectAsync(address.Host,address.Port,timeout.Token).ConfigureAwait(false);var stream=client.GetStream();
        using var handshake=new MemoryStream();WriteInt(handshake,0);WriteInt(handshake,-1);
        var hostname=Encoding.UTF8.GetBytes(address.Host);WriteInt(handshake,hostname.Length);handshake.Write(hostname);
        handshake.WriteByte((byte)(address.Port>>8));handshake.WriteByte((byte)address.Port);WriteInt(handshake,1);
        using var request=new MemoryStream();WriteInt(request,(int)handshake.Length);handshake.Position=0;handshake.CopyTo(request);request.Write([1,0]);
        await stream.WriteAsync(request.ToArray(),timeout.Token).ConfigureAwait(false);
        var length=await ReadIntAsync(stream,timeout.Token).ConfigureAwait(false);if(length is <3 or >1048576)throw new InvalidDataException("服务器响应大小无效。");
        var packet=new byte[length];await stream.ReadExactlyAsync(packet,timeout.Token).ConfigureAwait(false);
        using var content=new MemoryStream(packet);if(await ReadIntAsync(content,timeout.Token)!=0)throw new InvalidDataException("不是 Minecraft 状态响应。");
        var jsonLength=await ReadIntAsync(content,timeout.Token);if(jsonLength<0||jsonLength!=content.Length-content.Position)throw new InvalidDataException("服务器状态数据不完整。");
        using var document=JsonDocument.Parse(packet.AsMemory((int)content.Position,jsonLength),new JsonDocumentOptions{MaxDepth=32});var root=document.RootElement;
        if(root.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("服务器状态格式无效。");
        var version=root.TryGetProperty("version",out var v)&&v.ValueKind==JsonValueKind.Object&&v.TryGetProperty("name",out var name)&&name.ValueKind==JsonValueKind.String?name.GetString()??"未知版本":"未知版本";
        var online=0;var maximum=0;
        if(root.TryGetProperty("players",out var players)&&players.ValueKind==JsonValueKind.Object){if(players.TryGetProperty("online",out var o)&&o.ValueKind==JsonValueKind.Number)o.TryGetInt32(out online);if(players.TryGetProperty("max",out var m)&&m.ValueKind==JsonValueKind.Number)m.TryGetInt32(out maximum);}
        return new(version,online,maximum,root.TryGetProperty("description",out var description)?LanWorld.StripFormatting(Text(description)):"Minecraft 世界",clock.ElapsedMilliseconds);
    }
    private static string Text(JsonElement value)
    {
        if(value.ValueKind==JsonValueKind.String)return value.GetString()??"";
        if(value.ValueKind==JsonValueKind.Array)return string.Concat(value.EnumerateArray().Select(Text));
        if(value.ValueKind!=JsonValueKind.Object)return "";
        return (value.TryGetProperty("text",out var text)?Text(text):"")+(value.TryGetProperty("extra",out var extra)?Text(extra):"");
    }
    private static void WriteInt(Stream stream,int value)
    {var current=(uint)value;do{var part=(byte)(current&127);current>>=7;if(current!=0)part|=128;stream.WriteByte(part);}while(current!=0);}
    private static async Task<int> ReadIntAsync(Stream stream,CancellationToken ct)
    {var value=0;var b=new byte[1];for(var i=0;i<5;i++){await stream.ReadExactlyAsync(b,ct);value|=(b[0]&127)<<(7*i);if((b[0]&128)==0)return value;}throw new InvalidDataException("服务器响应格式无效。");}
}
