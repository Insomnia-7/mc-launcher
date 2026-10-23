using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Launcher.Application.Update;

/// <summary>
/// 更新地址的持久化位置：<c>{数据目录}/settings.json</c> 的 <c>updateUrl</c>。
/// 环境变量 <c>MCLAUNCHER_UPDATE_URL</c> 优先级更高，由 <c>IUpdateClient</c> 自己看。
/// </summary>
public static class UpdateSettings
{
    public const string Key = "updateUrl";

    public static string? ReadUrl(string dataDirectory)
    {
        try
        {
            var path = Path.Combine(dataDirectory, "settings.json");
            if (!File.Exists(path))
                return null;

            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            return root?[Key]?.GetValue<string>() is { Length: > 0 } value ? value : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>写回地址；传空串表示删掉这个键（保留 settings.json 里的其它内容）。</summary>
    public static void WriteUrl(string dataDirectory, string? url)
    {
        var path = Path.Combine(dataDirectory, "settings.json");
        JsonObject root;
        try
        {
            root = (File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null) ?? new JsonObject();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            root = new JsonObject();
        }

        if (string.IsNullOrWhiteSpace(url))
            root.Remove(Key);
        else
            root[Key] = url;

        Directory.CreateDirectory(dataDirectory);
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
