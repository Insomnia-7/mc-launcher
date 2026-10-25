using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Launcher.Application.Multiplayer;

namespace Launcher.Infrastructure.Multiplayer;

public interface ILanWorldDiscovery
{
    /// <summary>在给定时间内收集局域网广播，按 主机:端口 去重（保留最后一次广播）。</summary>
    Task<IReadOnlyList<LanWorld>> DiscoverAsync(TimeSpan window, CancellationToken cancellationToken = default);
}

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
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        try
        {
            client.Client.Bind(new IPEndPoint(IPAddress.Any, port));
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
                var payload = System.Text.Encoding.UTF8.GetString(received.Buffer);
                var world = LanWorld.TryParse(payload, received.RemoteEndPoint.Address.ToString(), DateTimeOffset.Now);
                if (world is null) continue;

                found[world.Endpoint] = world; // 同一端点重复广播时以最新时间为准
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

        return found.Values
            .OrderBy(world => world.CleanMotd, StringComparer.CurrentCulture)
            .ToList();
    }
}
