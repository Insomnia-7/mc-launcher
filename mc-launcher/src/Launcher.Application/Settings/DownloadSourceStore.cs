using System.Text.Json;
using System.Text.Json.Nodes;

namespace Launcher.Application.Settings;

/// <summary>
/// <c>settings.json</c> 里的 <c>downloadSource</c> 读写。照着 <c>CurseForgeKeyStore</c> 的做法：
/// 用 JsonNode 改单个键，文件里其它字段（例如 curseForgeApiKey）原样保留。
/// 读不出来 / 写坏了都按官方源处理，不让配置问题拦住安装。
/// </summary>
public static class DownloadSourceStore
{
    public const string SettingsKey = "downloadSource";

    /// <summary>当前数据目录里的下载源，缺省 Official。</summary>
    public static DownloadSource Read(string? dataDirectory = null) =>
        ReadFile(LauncherDataPaths.ResolveSettingsFile(dataDirectory));

    /// <summary>读指定 settings.json；不存在、损坏、值不认识都返回 Official。</summary>
    public static DownloadSource ReadFile(string settingsPath)
    {
        if (string.IsNullOrWhiteSpace(settingsPath) || !File.Exists(settingsPath))
        {
            return DownloadSource.Official;
        }

        try
        {
            if (JsonNode.Parse(File.ReadAllText(settingsPath)) is JsonObject root &&
                root[SettingsKey] is JsonValue value &&
                value.TryGetValue<string>(out var text))
            {
                return DownloadSourceText.ParseOrDefault(text);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            // 配置损坏不影响其它功能，当作没配
        }

        return DownloadSource.Official;
    }

    /// <summary>写回当前数据目录的 settings.json。</summary>
    public static void Write(DownloadSource source, string? dataDirectory = null) =>
        WriteFile(LauncherDataPaths.ResolveSettingsFile(dataDirectory), source);

    /// <summary>写回指定 settings.json，保留文件里其它键。</summary>
    public static void WriteFile(string settingsPath, DownloadSource source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);

        var directory = Path.GetDirectoryName(settingsPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        JsonObject root;
        try
        {
            root = File.Exists(settingsPath) && JsonNode.Parse(File.ReadAllText(settingsPath)) is JsonObject existing
                ? existing
                : new JsonObject();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            root = new JsonObject();
        }

        root[SettingsKey] = source.ToToken();
        File.WriteAllText(settingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
