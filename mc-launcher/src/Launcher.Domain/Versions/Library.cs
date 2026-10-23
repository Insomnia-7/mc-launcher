using System.Text.Json;
using System.Text.Json.Serialization;

namespace Launcher.Domain.Versions;

/// <summary>libraries[] 的一项。name 形如 group:artifact:version[:classifier]。</summary>
public sealed class Library
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("downloads")]
    public LibraryDownloads? Downloads { get; init; }

    /// <summary>老版本（≤1.12）才有：{"windows":"natives-windows", "osx":"natives-osx"}。</summary>
    [JsonPropertyName("natives")]
    public Dictionary<string, string>? Natives { get; init; }

    [JsonPropertyName("rules")]
    public List<Rule>? Rules { get; init; }

    [JsonPropertyName("extract")]
    public ExtractRule? Extract { get; init; }

    /// <summary>部分加载器库只给 maven 基址 + name，没有 downloads 节点。</summary>
    [JsonPropertyName("url")]
    public string? MavenBaseUrl { get; init; }

    public string GroupId => Parts.Length > 0 ? Parts[0] : "";
    public string ArtifactId => Parts.Length > 1 ? Parts[1] : "";
    public string Version => Parts.Length > 2 ? Parts[2] : "";

    private string[] Parts => _parts ??= Name.Split(':');
    private string[]? _parts;

    /// <summary>group:artifact:version → pom 坐标路径（用 / 分隔）。</summary>
    public string MavenPath(string? classifier = null, string extension = "jar")
    {
        var g = GroupId.Replace('.', '/');
        var file = $"{ArtifactId}-{Version}" + (classifier is null ? "" : $"-{classifier}") + "." + extension;
        return $"{g}/{ArtifactId}/{Version}/{file}";
    }

    public override string ToString() => Name;
}

public sealed class LibraryDownloads
{
    [JsonPropertyName("artifact")]
    public DownloadArtifact? Artifact { get; init; }

    [JsonPropertyName("classifiers")]
    public Dictionary<string, DownloadArtifact>? Classifiers { get; init; }
}

public sealed class DownloadArtifact
{
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("sha1")]
    public string? Sha1 { get; init; }

    [JsonPropertyName("size")]
    public long Size { get; init; }

    [JsonPropertyName("url")]
    public string Url { get; init; } = "";

    public override string ToString() => $"{Path ?? Url} ({Size:N0} B)";
}

public sealed class ExtractRule
{
    [JsonPropertyName("exclude")]
    public List<string>? Exclude { get; init; }
}

/// <summary>把 "字符串 | 带 rules 的对象" 统一读成 ArgumentValue。</summary>
public sealed class ArgumentValueConverter : JsonConverter<ArgumentValue>
{
    public override ArgumentValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return new ArgumentValue { Value = reader.GetString() };

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException($"arguments 元素应为字符串或对象，实际是 {reader.TokenType}");

        string? value = null;
        List<Rule>? rules = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;

            var prop = reader.GetString();
            reader.Read();

            switch (prop)
            {
                case "value" when reader.TokenType == JsonTokenType.String:
                    value = reader.GetString();
                    break;
                case "value" when reader.TokenType == JsonTokenType.StartArray:
                    // 少数版本把 value 写成数组：整段跳过，不值得为它冒解析风险
                    reader.Skip();
                    value = null;
                    break;
                case "rules":
                    rules = JsonSerializer.Deserialize<List<Rule>>(ref reader, options);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        return new ArgumentValue { Value = value, Rules = rules };
    }

    public override void Write(Utf8JsonWriter writer, ArgumentValue value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.Value is not null) writer.WriteString("value", value.Value);
        if (value.Rules is not null)
        {
            writer.WritePropertyName("rules");
            JsonSerializer.Serialize(writer, value.Rules, options);
        }
        writer.WriteEndObject();
    }
}
