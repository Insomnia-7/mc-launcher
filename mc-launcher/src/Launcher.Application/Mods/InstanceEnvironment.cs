using System.Text.Json;
using Launcher.Application.Install;
using Launcher.Domain.Versions;

namespace Launcher.Application.Mods;

/// <summary>实例的运行环境：游戏本体版本 + 加载器。装 Mod 前必须知道这两个。</summary>
public sealed record InstanceEnvironment(
    string InstanceId,
    string GameVersion,
    string? Loader,
    string? LoaderVersion)
{
    /// <summary>Modrinth 用的加载器 slug（fabric / forge / quilt / neoforge），原版返回 null。</summary>
    public string? LoaderSlug => Loader?.ToLowerInvariant() switch
    {
        "fabric" => "fabric",
        "forge" => "forge",
        "quilt" => "quilt",
        "neoforge" => "neoforge",
        _ => null,
    };

    /// <summary>人话：「Fabric 0.20.1 / 1.21.4」，用在状态栏和对话框标题上。</summary>
    public string Describe() =>
        Loader is null ? $"{GameVersion}（原版）" : $"{Loader} {LoaderVersion} / {GameVersion}";

    /// <summary>
    /// 从磁盘推断实例环境：<c>versions/&lt;id&gt;/&lt;id&gt;.json</c> 的 inheritsFrom 是权威父版本，
    /// 加载器名字和版本从目录名反推（VersionIdParser），instance.json 里的记录覆盖游戏版本。
    /// </summary>
    public static InstanceEnvironment Resolve(string gameRoot, string instanceId)
    {
        string? parent = null;
        var versionJson = Path.Combine(gameRoot, "versions", instanceId, instanceId + ".json");

        try
        {
            if (File.Exists(versionJson))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(versionJson));
                if (document.RootElement.TryGetProperty("inheritsFrom", out var inherits))
                    parent = inherits.GetString();
            }
        }
        catch (JsonException)
        {
            // 版本文件坏了就当没有，loaders 反推仍然能给出大部分信息
        }

        var parsed = VersionIdParser.Parse(instanceId, parent);
        var metadata = InstanceMetadata.TryRead(gameRoot, instanceId);

        var gameVersion = metadata?.MinecraftVersion ?? parsed.GameVersion;
        return new InstanceEnvironment(instanceId, gameVersion, parsed.Loader, parsed.LoaderVersion);
    }
}
