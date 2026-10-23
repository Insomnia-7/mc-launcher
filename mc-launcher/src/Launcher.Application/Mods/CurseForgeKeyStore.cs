using System.Text.Json;
using System.Text.Json.Nodes;

namespace Launcher.Application.Mods;

/// <summary>
/// CurseForge API Key 的存取。优先级：环境变量 &gt; 数据目录下的 settings.json。
/// Key 在 https://console.curseforge.com/ 免费申请（Core 计划）。
/// </summary>
public static class CurseForgeKeyStore
{
    public const string EnvironmentVariable = "CURSEFORGE_API_KEY";
    public const string SettingsFileName = "settings.json";
    private const string SettingsKey = "curseForgeApiKey";

    /// <summary>读出当前生效的 Key，没有则返回 null。</summary>
    public static string? Resolve(string dataDirectory)
    {
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } fromEnv &&
            fromEnv.Trim() is { Length: > 0 } trimmed)
        {
            return trimmed;
        }

        var path = Path.Combine(dataDirectory, SettingsFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is JsonObject root &&
                root[SettingsKey]?.GetValue<string>() is { Length: > 0 } fromFile)
            {
                return fromFile.Trim();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            // 配置损坏不影响其它功能，当作没配
        }

        return null;
    }

    /// <summary>把 Key 写回 settings.json，保留文件里其它字段。传空字符串表示清除。</summary>
    public static void Save(string dataDirectory, string? apiKey)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, SettingsFileName);

        JsonObject root;
        try
        {
            root = File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject existing
                ? existing
                : new JsonObject();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            root = new JsonObject();
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            root.Remove(SettingsKey);
        }
        else
        {
            root[SettingsKey] = apiKey.Trim();
        }

        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
