using System.Text.Json;
using Launcher.Application.Experience;

namespace Launcher.Application.Instances;

public sealed record GameLibraryCandidate(string Path, int Versions, bool IsFangyu)
{
    public string Summary => $"{Versions} 个版本 · {(IsFangyu ? "方隅游戏库" : "外部游戏库，复制导入后使用")}";
}

public static class GameLibraryDiscovery
{
    public static bool IsFangyuLibrary(string root)
    {
        try
        {
            if (File.Exists(System.IO.Path.Combine(root,".fangyu-library"))) return true;
            var instances=System.IO.Path.Combine(root,"instances");
            return Directory.Exists(instances) && Directory.GetDirectories(instances).Any(d=>File.Exists(System.IO.Path.Combine(d,"instance.json")) || Directory.Exists(System.IO.Path.Combine(d,".minecraft")));
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { return false; }
    }
    public static IReadOnlyList<string> Versions(string root)
    {
        var result = new List<string>();
        try
        {
            ExperienceFiles.RejectLinks(root);
            var versions = System.IO.Path.Combine(root, "versions");
            if (!Directory.Exists(versions)) return result;
            ExperienceFiles.RejectLinks(versions);
            foreach (var folder in Directory.GetDirectories(versions))
            {
                try
                {
                    ExperienceFiles.RejectLinks(folder);
                    var id = System.IO.Path.GetFileName(folder);
                    var file = System.IO.Path.Combine(folder, id + ".json");
                    ExperienceFiles.RejectLinks(file);
                    using var json = JsonDocument.Parse(File.ReadAllText(file));
                    if (json.RootElement.TryGetProperty("id", out var value) && value.GetString() == id) result.Add(id);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return result.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public static IReadOnlyList<GameLibraryCandidate> Scan(IEnumerable<string> roots, int depth, CancellationToken ct)
    {
        var result = new List<GameLibraryCandidate>();
        var comparison = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparison);
        var pending = new Queue<(string Path, int Depth)>();
        foreach (var root in roots.Where(Directory.Exists)) pending.Enqueue((System.IO.Path.GetFullPath(root), 0));
        // Search only selected/common roots. Never follow links or recurse through libraries, caches or game saves.
        var ignored = new HashSet<string>([".cache", "cache", "assets", "libraries", "versions", "instances", "saves", "mods", ".git", "node_modules", "bin", "obj"], StringComparer.OrdinalIgnoreCase);
        while (pending.TryDequeue(out var next))
        {
            ct.ThrowIfCancellationRequested();
            if (!seen.Add(next.Path)) continue;
            try
            {
                ExperienceFiles.RejectLinks(next.Path);
                var versions = Versions(next.Path);
                if (versions.Count > 0)
                { result.Add(new(next.Path, versions.Count, IsFangyuLibrary(next.Path))); continue; }
                if (next.Depth >= depth) continue;
                foreach (var dir in Directory.GetDirectories(next.Path))
                    if (!ignored.Contains(System.IO.Path.GetFileName(dir))) pending.Enqueue((dir, next.Depth + 1));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return result.OrderBy(c => c.Path, comparison).ToArray();
    }
}
