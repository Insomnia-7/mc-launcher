using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Launcher.Application.Update;

/// <summary>更新清单里的一个文件。Path 是相对启动器根目录的路径，用 / 分隔。</summary>
public sealed record UpdateFile(string Path, string Url, string Sha256, long Size);

/// <summary>
/// 启动器自己的更新清单。设计上只依赖一个静态 JSON —— 不搞渠道、不搞灰度，
/// 想发版就往这个地址覆盖一份新的。
/// </summary>
public sealed record UpdateManifest(
    string Version,
    IReadOnlyList<UpdateFile> Files,
    string? Notes = null,
    DateTimeOffset? PublishedAt = null,
    string? Entry = null,
    string? Runtime = null)
{
    [JsonIgnore]
    public UpdateSignatureProof? SignatureProof { get; init; }

    /// <summary>重启时优先用它；没写就用调用方给的默认值。</summary>
    public string EntryOrDefault(string fallback) =>
        string.IsNullOrWhiteSpace(Entry) ? fallback : Entry!;

    public long TotalBytes => Files.Sum(file => Math.Max(0, file.Size));

    public string ToSummary(string? currentVersion)
    {
        var size = TotalBytes / 1024d / 1024d;
        return $"{currentVersion ?? "?"} → {Version}（{Files.Count} 个文件，{size:N1} MB）";
    }
}

public sealed class UpdateManifestException : Exception
{
    public UpdateManifestException(string message) : base(message) { }
    public UpdateManifestException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>清单解析与版本比较。放 Application 层是为了让 Updater 和 App 共用同一套规则。</summary>
public static class UpdateManifestParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static UpdateManifest Parse(string json)
    {
        RawManifest? raw;
        try
        {
            raw = JsonSerializer.Deserialize<RawManifest>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new UpdateManifestException("更新清单不是合法 JSON：" + ex.Message);
        }

        if (raw is null)
            throw new UpdateManifestException("更新清单是空的");

        if (string.IsNullOrWhiteSpace(raw.Version))
            throw new UpdateManifestException("更新清单缺少 version 字段");

        var files = new List<UpdateFile>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in raw.Files ?? [])
        {
            if (string.IsNullOrWhiteSpace(file.Path))
                throw new UpdateManifestException("更新清单里有文件缺少 path");

            if (string.IsNullOrWhiteSpace(file.Url))
                throw new UpdateManifestException($"更新清单里 {file.Path} 缺少 url");

            var normalizedPath = file.Path.Replace((char)92, '/');
            // 先统一分隔符，再检查根路径及别名，不能在检查后才转换。
            if (normalizedPath.Contains("..", StringComparison.Ordinal) ||
                normalizedPath.StartsWith('/') ||
                normalizedPath.Contains(':', StringComparison.Ordinal) ||
                normalizedPath.Split('/').Any(segment => segment.Length == 0 || segment == "."))
                throw new UpdateManifestException($"更新清单里 {file.Path} 不是相对路径");

            if (!paths.Add(normalizedPath)) throw new UpdateManifestException($"更新清单有重复目标：{file.Path}");

            files.Add(new UpdateFile(
                normalizedPath,
                file.Url,
                (file.Sha256 ?? string.Empty).Trim().ToLowerInvariant(),
                file.Size));
        }

        if (files.Count == 0)
            throw new UpdateManifestException("更新清单里没有任何文件");

        return new UpdateManifest(raw.Version.Trim(), files, raw.Notes, raw.PublishedAt, raw.Entry, raw.Runtime);
    }

    private sealed record RawManifest(
        [property: JsonPropertyName("version")] string? Version,
        [property: JsonPropertyName("files")] List<RawFile>? Files,
        [property: JsonPropertyName("notes")] string? Notes,
        [property: JsonPropertyName("publishedAt")] DateTimeOffset? PublishedAt,
        [property: JsonPropertyName("entry")] string? Entry,
        [property: JsonPropertyName("runtime")] string? Runtime);

    private sealed record RawFile(
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("sha256")] string? Sha256,
        [property: JsonPropertyName("size")] long Size);
}

/// <summary>宽松的版本比较：支持 <c>0.2.0</c>、<c>v0.2.0</c>、<c>0.2.0-beta.1</c>。</summary>
public static class UpdateVersion
{
    public static bool IsNewer(string candidate, string? current)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return false;

        if (string.IsNullOrWhiteSpace(current))
            return true;

        var left = Tokenize(candidate);
        var right = Tokenize(current);

        for (var index = 0; index < Math.Max(left.Count, right.Count); index++)
        {
            var a = index < left.Count ? left[index] : "0";
            var b = index < right.Count ? right[index] : "0";

            if (a == b)
                continue;

            var aNumber = int.TryParse(a, out var an);
            var bNumber = int.TryParse(b, out var bn);

            if (aNumber && bNumber)
                return an > bn;

            // 数字比预发布标识新：1.0.0 > 1.0.0-beta
            if (aNumber != bNumber)
                return aNumber;

            return string.CompareOrdinal(a, b) > 0;
        }

        return false;
    }

    private static List<string> Tokenize(string version)
    {
        var text = version.Trim();
        // SemVer 构建元数据不参与比较，避免提交号导致同版本被误判为新版。
        var metadata = text.IndexOf('+');
        if(metadata >= 0) text = text[..metadata];
        if (text.StartsWith('v') || text.StartsWith('V'))
            text = text[1..];

        return text
            .Split(['.', '-', '+', '_'], StringSplitOptions.RemoveEmptyEntries)
            .ToList();
    }
}
