using System.Text.Json;
using Launcher.Application.Install;

namespace Launcher.Application.Instances;

public sealed record InstanceDeletionResult(bool VersionRetained,string? CleanupWarning=null);

/// <summary>Delete private data and unreferenced version/native files. Shared assets/libraries remain.</summary>
public static class InstanceDeletion
{
    public static InstanceDeletionResult Delete(string gameRoot, string instanceId, string versionId)
    {
        var instancePath = Child(gameRoot, "instances", instanceId);
        var versionPath = Child(gameRoot, "versions", versionId);
        var nativePath = Child(gameRoot, "natives", versionId);
        // Snapshot BEFORE deleting: re-scanning versions afterwards would resurrect a virtual instance.
        var registry = InstanceRegistry.LoadOrMigrate(gameRoot);
        var dependencies=new List<string>();
        void Visit(string id)
        {
            foreach(var parent in InstanceRegistry.Parents(gameRoot,id))
                if(!parent.Equals(versionId,StringComparison.OrdinalIgnoreCase)&&!dependencies.Contains(parent,StringComparer.OrdinalIgnoreCase))
                { dependencies.Add(parent);Visit(parent); }
        }
        Visit(versionId);
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
        // 墓碑必须先写：删除过程中程序异常退出时，versions/<id>/<id>.json 可能只删了一半，
        // 或者被其它加载器版本继承而保留（retained=true）。只要墓碑在，LoadCore 就不会把它补成
        // 虚拟实例「复活」。重装同版本时 GameInstaller / Registry.Register 会清除墓碑。
        InstanceRegistry.HideVersion(gameRoot, versionId, instanceId);
        DeleteDirectory(instancePath);
        if (!retained) { DeleteDirectory(versionPath); DeleteDirectory(nativePath); }
        // 版本目录确实删干净了就回收版本墓碑，避免 hidden-versions.json 只增不减。
        // retained（被其它加载器版本继承）时必须保留，否则虚拟条目会复活。
        // 实例墓碑始终保留：防止半删残留的 instances/<id> 复活，重装时由 UnhideVersion 清除。
        if (!retained && !Directory.Exists(versionPath)) InstanceRegistry.UnhideVersion(gameRoot, versionId);
        registry.Remove(instanceId);
        // Only dependency versions are collected. Explicit vanilla installations and
        // ancestors referenced by another surviving loader/pack remain intact.
        foreach(var dependency in dependencies)
        {
            try
            {
            if(InstanceMetadata.TryRead(gameRoot,dependency) is not null)continue;
            InstanceRegistry.RecordDependency(gameRoot,dependency);
            var used=InstanceRegistry.Load(gameRoot).Instances.Any(r=>r.VersionId.Equals(dependency,StringComparison.OrdinalIgnoreCase));
            if(!used&&Directory.Exists(versionsRoot))
                used=Directory.EnumerateDirectories(versionsRoot).Any(d=>
                    !Path.GetFileName(d).Equals(dependency,StringComparison.OrdinalIgnoreCase)
                    &&InstanceRegistry.Parents(gameRoot,Path.GetFileName(d)).Contains(dependency,StringComparer.OrdinalIgnoreCase));
            if(!used)
            {
                DeleteDirectory(Child(gameRoot,"versions",dependency));
                DeleteDirectory(Child(gameRoot,"natives",dependency));
            }
            }
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or JsonException)
            {return new(retained,"实例已删除，基础版本清理未完成："+ex.Message);}
        }
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
        if (!Directory.Exists(path)) return;
        // 游戏进程或解压器留下的临时/日志文件常带只读位；先清属性再递归删，
        // 避免「文件被占用/只读」让删除停在半途还误报成功。
        ClearReadOnly(path);
        Directory.Delete(path, recursive: true);
    }

    private static void ClearReadOnly(string directory)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var attributes = File.GetAttributes(file);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
