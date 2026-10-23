using System.Text.Json;
using System.Text.Json.Nodes;

namespace Launcher.Application.Settings;

public enum ProxyMode { Automatic, Direct, Custom }

/// <summary>启动器网络设置；自定义模式不回退直连，不保存代理凭据。</summary>
public sealed record ProxySettings(ProxyMode Mode = ProxyMode.Automatic, string Address = "")
{
    public ProxySettings Validate()
    {
        if (!Enum.IsDefined(Mode)) throw new ArgumentException("请选择有效的代理模式。");
        if (Mode != ProxyMode.Custom) return this with { Address = "" };
        var address = Address?.Trim() ?? "";
        if (address.Any(char.IsWhiteSpace) || !Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https" or "socks5") || string.IsNullOrEmpty(uri.Host) ||
            uri.Port is < 1 or > 65535 || uri.AbsolutePath is not ("" or "/") ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
            throw new ArgumentException("请输入 HTTP、HTTPS 或 SOCKS5 代理地址和端口；不支持账号密码、路径或查询参数。");
        return this with { Address = uri.GetLeftPart(UriPartial.Authority) };
    }
}

public static class ProxySettingsStore
{
    public static ProxySettings Read(string? dataDirectory = null)
    {
        var path = LauncherDataPaths.ResolveSettingsFile(dataDirectory);
        if (!File.Exists(path)) return new();
        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new InvalidDataException("设置文件必须是 JSON 对象。");
        if (!root.TryGetPropertyValue("proxy", out var node)) return new();
        if (node is not JsonObject proxy || proxy["mode"] is not JsonValue modeNode ||
            !modeNode.TryGetValue<string>(out var mode))
            throw new InvalidDataException("代理配置格式无效，请在网络设置中重新保存。");
        var selected = mode switch
        {
            "automatic" => ProxyMode.Automatic,
            "direct" => ProxyMode.Direct,
            "custom" => ProxyMode.Custom,
            _ => throw new InvalidDataException("代理模式无效，请在网络设置中重新保存。")
        };
        var address = proxy["address"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
        return new ProxySettings(selected, address).Validate();
    }

    /// <summary>保留其它配置，临时文件写完后替换；失败不覆盖原文件。</summary>
    public static void Write(ProxySettings settings, string? dataDirectory = null)
    {
        var valid = settings.Validate();
        var path = LauncherDataPaths.ResolveSettingsFile(dataDirectory);
        var root = File.Exists(path)
            ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new InvalidDataException("设置文件格式无效，无法保存代理设置。")
            : new JsonObject();
        root["proxy"] = new JsonObject
        {
            ["mode"] = valid.Mode.ToString().ToLowerInvariant(),
            ["address"] = valid.Address
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".proxy-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
