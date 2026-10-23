using System.Text.Json;

namespace Launcher.Application.Instances;

public sealed record InstanceDeletionResult(bool VersionRetained);

/// <summary>Delete private data and unreferenced version/native files. Shared assets/libraries remain.</summary>
public static class InstanceDeletion
{
    public static InstanceDeletionResult Delete(string gameRoot, string instanceId, string versionId)
    {
        var instancePath = Child(gameRoot, "instances", instanceId);
        var versionPath = Child(gameRoot, "versions", versionId);
        var nativePath = Child(gameRoot, "natives", versionId);
        // Snapshot BEFORE deleting: re-scanning versions afterwards would resurrect a virtual instance.
        var registry = InstanceRegistry.Load(gameRoot);
        var retained = registry.Instances.Any(r => !r.Id.Equals(instanceId, StringComparison.OrdinalIgnoreCase)
            && r.VersionId.Equals(versionId, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(Child(gameRoot, "instances", r.Id)));
        var versionsRoot = Path.Combine(Path.GetFullPath(gameRoot), "versions");
        if (Directory.Exists(versionsRoot))
        {
            foreach (var directory in Directory.EnumerateDirectories(versionsRoot))
            {
                if (Path.GetFullPath(directory).Equals(versionPath, StringComparison.OrdinalIgnoreCase)) continue;
                var json = Path.Combine(directory, Path.GetFileName(directory) + ".json");
                if (!File.Exists(json)) continue;
                // Fail before deleting when dependencies cannot be inspected.
                using var doc = JsonDocument.Parse(File.ReadAllText(json));
                foreach (var key in new[] { "inheritsFrom", "x-launcher-parent" })
                    if (doc.RootElement.TryGetProperty(key, out var parent) && parent.ValueKind == JsonValueKind.String
                        && parent.GetString()?.Equals(versionId, StringComparison.OrdinalIgnoreCase) == true) retained = true;
            }
        }
        DeleteDirectory(instancePath);
        if (!retained) { DeleteDirectory(versionPath); DeleteDirectory(nativePath); }
        registry.Remove(instanceId);
        return new(retained);
    }

    private static string Child(string root, string category, string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id is "." or ".." || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || id.IndexOfAny(['/', '\\', ':']) >= 0) throw new ArgumentException("实例或版本 ID 非法");
        var parent = Path.Combine(Path.GetFullPath(root), category);
        var child = Path.GetFullPath(Path.Combine(parent, id));
        if (!child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("删除路径越界");
        // Do not follow user-created links to external game/save directories.
        foreach (var path in new[] { Path.GetFullPath(root), parent, child })
            if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("删除目标包含目录链接，请先在文件管理器中检查：" + path);
        return child;
    }
    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
