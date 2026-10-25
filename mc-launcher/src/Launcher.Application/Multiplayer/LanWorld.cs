using System.Globalization;

namespace Launcher.Application.Multiplayer;

/// <summary>
/// 局域网里发现的一个「对局域网开放」的世界。
/// 数据来自 Minecraft 客户端每秒广播的 UDP 包：<c>[MOTD]标题[/MOTD][AD]端口[/AD]</c>。
/// </summary>
public sealed record LanWorld(string Motd, string Host, int Port, DateTimeOffset SeenAt)
{
    public string Endpoint => $"{Host}:{Port}";

    /// <summary>去掉 §a 这类格式码，只留可读文字。</summary>
    public string CleanMotd => StripFormatting(Motd);

    public static string StripFormatting(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var builder = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '§' && i + 1 < text.Length)
            {
                i++; // 跳过格式码本体
                continue;
            }

            builder.Append(text[i]);
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// 解析广播内容。格式不对（比如别的程序占用了 4445）时返回 null，不抛异常。
    /// </summary>
    public static LanWorld? TryParse(string? payload, string? host, DateTimeOffset seenAt)
    {
        if (string.IsNullOrWhiteSpace(payload) || string.IsNullOrWhiteSpace(host)) return null;

        var motd = Between(payload, "[MOTD]", "[/MOTD]");
        var ad = Between(payload, "[AD]", "[/AD]");
        if (ad is null) return null;

        if (!int.TryParse(ad.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)) return null;
        if (port is < 1 or > 65535) return null;

        return new LanWorld(motd ?? string.Empty, host, port, seenAt);
    }

    private static string? Between(string text, string start, string end)
    {
        var begin = text.IndexOf(start, StringComparison.Ordinal);
        if (begin < 0) return null;

        begin += start.Length;
        var finish = text.IndexOf(end, begin, StringComparison.Ordinal);
        if (finish < 0) return null;

        return text[begin..finish];
    }

    public override string ToString() => $"{CleanMotd} · {Endpoint}";
}
