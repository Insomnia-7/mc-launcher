using System.Text.Json.Serialization;

namespace Launcher.Domain.Versions;

/// <summary>assets/indexes/&lt;id&gt;.json。</summary>
public sealed class AssetIndex
{
    [JsonPropertyName("objects")]
    public Dictionary<string, AssetObject> Objects { get; init; } = [];

    [JsonPropertyName("virtual")]
    public bool? Virtual { get; init; }

    [JsonPropertyName("map_to_resources")]
    public bool? MapToResources { get; init; }

    public long TotalSize => Objects.Values.Sum(o => o.Size);

    public int Count => Objects.Count;
}

public sealed class AssetObject
{
    [JsonPropertyName("hash")]
    public string Hash { get; init; } = "";

    [JsonPropertyName("size")]
    public long Size { get; init; }

    /// <summary>objects/ab/abcdef... 相对 assets 目录的路径。</summary>
    public string RelativePath => $"objects/{Hash[..2]}/{Hash}";
}
