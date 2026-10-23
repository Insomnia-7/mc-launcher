using System.Text.Json.Serialization;

namespace Launcher.Domain.Versions;

/// <summary>versions/&lt;id&gt;/&lt;id&gt;.json 的根对象（含 Fabric/Forge 等 inheritsFrom 增量文件）。</summary>
public sealed class VersionJson
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("mainClass")]
    public string? MainClass { get; init; }

    /// <summary>非 null 表示这是加载器增量文件，需要与父版本合并。</summary>
    [JsonPropertyName("inheritsFrom")]
    public string? InheritsFrom { get; init; }

    [JsonPropertyName("assets")]
    public string? Assets { get; init; }

    [JsonPropertyName("assetIndex")]
    public AssetIndexRef? AssetIndex { get; init; }

    [JsonPropertyName("downloads")]
    public VersionDownloads? Downloads { get; init; }

    [JsonPropertyName("javaVersion")]
    public JavaVersionRef? JavaVersion { get; init; }

    [JsonPropertyName("libraries")]
    public List<Library> Libraries { get; init; } = [];

    [JsonPropertyName("arguments")]
    public Arguments? Arguments { get; init; }

    /// <summary>1.12 及更早版本没有 arguments 对象，只有这一条空格分隔的字符串。</summary>
    [JsonPropertyName("minecraftArguments")]
    public string? MinecraftArguments { get; init; }

    [JsonPropertyName("complianceLevel")]
    public int? ComplianceLevel { get; init; }

    /// <summary>原样保留字符串：Fabric profile 用的是 "+0000" 时区写法，DateTimeOffset 直接解析会炸。</summary>
    [JsonPropertyName("releaseTime")]
    public string? ReleaseTime { get; init; }

    [JsonPropertyName("time")]
    public string? Time { get; init; }

    /// <summary>
    /// 非官方字段（x-launcher-parent）：合并后的加载器版本记住自己的父版本 id。
    /// 用来定位真正的 client.jar —— Fabric 只认父版本的 jar，重复的副本会让它 remap 失败。
    /// </summary>
    [JsonPropertyName("x-launcher-parent")]
    public string? LauncherParentId { get; init; }

    public override string ToString() => $"{Id} (mainClass={MainClass ?? "?"})";
}

public sealed class AssetIndexRef
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("sha1")]
    public string? Sha1 { get; init; }

    [JsonPropertyName("size")]
    public long Size { get; init; }

    [JsonPropertyName("totalSize")]
    public long TotalSize { get; init; }

    [JsonPropertyName("url")]
    public string Url { get; init; } = "";
}

public sealed class VersionDownloads
{
    [JsonPropertyName("client")]
    public DownloadArtifact? Client { get; init; }

    [JsonPropertyName("server")]
    public DownloadArtifact? Server { get; init; }

    [JsonPropertyName("client_mappings")]
    public DownloadArtifact? ClientMappings { get; init; }
}

public sealed class JavaVersionRef
{
    [JsonPropertyName("component")]
    public string? Component { get; init; }

    [JsonPropertyName("majorVersion")]
    public int MajorVersion { get; init; }
}

public sealed class Arguments
{
    [JsonPropertyName("game")]
    public List<ArgumentValue> Game { get; init; } = [];

    [JsonPropertyName("jvm")]
    public List<ArgumentValue> Jvm { get; init; } = [];
}

/// <summary>arguments 数组的元素：可以是字符串，也可以是带 rules 的条件对象。</summary>
[JsonConverter(typeof(ArgumentValueConverter))]
public sealed class ArgumentValue
{
    public string? Value { get; init; }
    public List<Rule>? Rules { get; init; }

    public static implicit operator ArgumentValue(string value) => new() { Value = value };
}
