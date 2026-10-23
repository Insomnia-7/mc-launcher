using System.Text.Json.Serialization;

namespace Launcher.Domain.Versions;

/// <summary>version_manifest_v2.json 的根对象。</summary>
public sealed class VersionManifest
{
    [JsonPropertyName("latest")]
    public LatestVersions? Latest { get; init; }

    [JsonPropertyName("versions")]
    public List<ManifestEntry> Versions { get; init; } = [];
}

public sealed class LatestVersions
{
    [JsonPropertyName("release")]
    public string? Release { get; init; }

    [JsonPropertyName("snapshot")]
    public string? Snapshot { get; init; }
}

public sealed class ManifestEntry
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("url")]
    public string Url { get; init; } = "";

    [JsonPropertyName("sha1")]
    public string? Sha1 { get; init; }

    [JsonPropertyName("releaseTime")]
    public DateTimeOffset ReleaseTime { get; init; }

    public override string ToString() => $"{Id} ({Type})";
}
