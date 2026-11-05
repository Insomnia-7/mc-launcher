using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using Launcher.Application.Multiplayer;

namespace Launcher.Infrastructure.Multiplayer;

/// <summary>
/// 监听 Minecraft「对局域网开放」的 UDP 广播（默认 4445）。
/// 只读监听，不发送任何包，也不影响游戏本身。
/// </summary>
public sealed class LanWorldDiscovery(int port = 4445) : ILanWorldDiscovery
{
    public const int DefaultPort = 4445;

    public async Task<IReadOnlyList<LanWorld>> DiscoverAsync(TimeSpan window, CancellationToken cancellationToken = default)
    {
        if (window <= TimeSpan.Zero) window = TimeSpan.FromSeconds(3);

        var found = new ConcurrentDictionary<string, LanWorld>(StringComparer.OrdinalIgnoreCase);

        using var client = new UdpClient(AddressFamily.InterNetwork);
        client.ExclusiveAddressUse=false;
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        var addresses=NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up)
            .SelectMany(n=>n.GetIPProperties().UnicastAddresses).Where(a=>a.Address.AddressFamily==AddressFamily.InterNetwork).Select(a=>a.Address).Distinct().ToArray();

        try
        {
            client.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            foreach(var address in addresses)
                try{client.JoinMulticastGroup(IPAddress.Parse("224.0.2.60"),address);}catch(SocketException){ }
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException(
                $"监听 UDP {port} 失败（可能被其它程序占用）：{ex.Message}", ex);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(window);

        try
        {
            while (!deadline.IsCancellationRequested)
            {
                var received = await client.ReceiveAsync(deadline.Token).ConfigureAwait(false);
                if(received.Buffer.Length>4096)continue;
                var payload = System.Text.Encoding.UTF8.GetString(received.Buffer);
                var world = LanWorld.TryParse(payload, received.RemoteEndPoint.Address.ToString(), DateTimeOffset.Now);
                if (world is null) continue;

                found[world.Endpoint] = world with{IsLocal=IPAddress.IsLoopback(received.RemoteEndPoint.Address)||addresses.Contains(received.RemoteEndPoint.Address)};
            }
        }
        catch (OperationCanceledException)
        {
            // 到点收工，正常路径
        }
        finally
        {
            client.Close();
        }

        cancellationToken.ThrowIfCancellationRequested();

        return found.Values
            .OrderBy(world => world.CleanMotd, StringComparer.CurrentCulture)
            .ToList();
    }
}
