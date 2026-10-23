using System.Text.Json;
using System.Text.Json.Serialization;
using Launcher.Application.Install;

namespace Launcher.Application.Update;

/// <summary>
/// 更新器「装过哪些文件」的账本，落在启动器目录的 <c>installed-files.json</c>。
/// 有了它才能在下一次更新时删掉上游已经移除的文件——只删自己写过的，绝不动用户自己放进去的东西。
/// </summary>
public sealed record InstalledFileSet(string Version, IReadOnlyList<string> Files)
{
    public const string FileName = "installed-files.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string PathFor(string targetRoot) => Path.Combine(targetRoot, FileName);

    /// <summary>读账本；文件不存在或读坏都返回 null（宁可这次不删，也不要误删）。</summary>
    public static InstalledFileSet? Load(string targetRoot)
    {
        try
        {
            var path = PathFor(targetRoot);
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<StoredFileSet>(text, SerializerOptions);
            if (parsed?.Files is null) return null;
            return new InstalledFileSet(parsed.Version ?? string.Empty, parsed.Files);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Save(string targetRoot)
    {
        Directory.CreateDirectory(targetRoot);
        var payload = new StoredFileSet { Version = Version, Files = Files.ToList() };
        File.WriteAllText(PathFor(targetRoot), JsonSerializer.Serialize(payload, SerializerOptions));
    }

    public static InstalledFileSet FromManifest(UpdateManifest manifest) =>
        new(manifest.Version, manifest.Files.Select(file => Normalize(file.Path)).ToList());

    /// <summary>上一次装过、这一次清单里没有的文件（相对路径，已去重并过滤掉不安全项）。</summary>
    public static IReadOnlyList<string> ComputeStale(InstalledFileSet? previous, IReadOnlyList<UpdateFile> current)
    {
        if (previous is null || previous.Files.Count == 0) return [];

        var keep = new HashSet<string>(current.Select(file => Normalize(file.Path)), StringComparer.OrdinalIgnoreCase);
        var stale = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in previous.Files)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var relative = Normalize(raw);
            if (string.Equals(relative, FileName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsSafeRelativePath(relative)) continue;
            if (keep.Contains(relative)) continue;
            if (!seen.Add(relative)) continue;
            stale.Add(relative);
        }

        return stale;
    }

    /// <summary>只接受 target 目录内的相对路径：拒绝绝对路径、盘符、<c>..</c> 越界与空段。</summary>
    public static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.Length > 400) return false;
        if (Path.IsPathRooted(path)) return false;
        if (path.Contains(':', StringComparison.Ordinal)) return false;
        if (path.Contains('\0')) return false;

        var segments = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return false;
        foreach (var segment in segments)
        {
            if (segment is "." or "..") return false;
        }

        return true;
    }

    public static string Normalize(string path) => path.Replace('\\', '/').Trim();

    private sealed class StoredFileSet
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("files")]
        public List<string>? Files { get; set; }
    }
}
