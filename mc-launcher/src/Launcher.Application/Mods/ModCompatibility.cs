using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Launcher.Application.Experience;

namespace Launcher.Application.Mods;

/// <summary>Offline checks of declared client dependencies; unknown version syntax is a warning, never a guessed incompatibility.</summary>
public static class ModCompatibility
{
    private sealed record Dependency(string Id, string Constraint, bool Conflict = false, bool Warning = false);
    private sealed record Entry(string File, string Id, string? Version, List<Dependency> Dependencies, bool Nested = false);

    public static IReadOnlyList<InspectionIssue> Inspect(IEnumerable<string> files, string gameVersion, string? loader,
        string? loaderVersion = null, int? javaVersion = null, CancellationToken ct = default)
    {
        var entries = new List<Entry>();
        var issues = new List<InspectionIssue>();
        foreach (var file in files.Where(p => p.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                ExperienceFiles.RejectLinks(file);
                using var archive = ZipFile.OpenRead(file);
                ReadArchive(archive, Path.GetFileName(file), loader, entries, issues, ct, 0);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
            { issues.Add(new("无法读取模组信息：" + Path.GetFileName(file), "文件损坏或元数据格式不受支持。", "重新下载该文件，或结合加载器日志检查。")); }
        }
        foreach (var group in entries.GroupBy(e => e.Id, StringComparer.Ordinal).Where(g => g.Where(e => !e.Nested).Select(e => e.File).Distinct().Count() > 1))
            issues.Add(new("重复模组：" + group.Key, string.Join("、", group.Select(e => e.File).Distinct()), "在模组工作台中只保留一个适配版本。", true));
        var versions = entries.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.Select(e => e.Version).Aggregate((a,b) => Compare(b ?? "",a ?? "") > 0 ? b : a), StringComparer.Ordinal);
        versions["minecraft"] = gameVersion;
        if (loader is not null) versions[loader == "fabric" ? "fabricloader" : loader == "quilt" ? "quilt_loader" : loader] = loaderVersion;
        if (loader == "quilt") versions["fabricloader"] = null; // Quilt provides Fabric loader compatibility, not a guessed version.
        if (javaVersion is not null) versions["java"] = javaVersion.ToString();
        foreach (var entry in entries.Where(e => !e.Nested || e.Version == versions.GetValueOrDefault(e.Id)))
        foreach (var dependency in entry.Dependencies)
        {
            if (dependency.Id == "java" && javaVersion is null) continue;
            var exists = versions.TryGetValue(dependency.Id, out var version);
            if (!exists)
            {
                if (!dependency.Conflict) issues.Add(new("缺少前置：" + dependency.Id, $"{entry.File} 声明需要 {dependency.Id} {dependency.Constraint}。", "在发现资源中搜索并安装与当前游戏和加载器匹配的前置；也可以禁用依赖它的模组。", true));
                continue;
            }
            var matches = Satisfies(version, dependency.Constraint);
            if (dependency.Conflict && matches == true)
                issues.Add(new("模组声明冲突：" + entry.Id + " / " + dependency.Id, $"{entry.File} 声明与 {dependency.Id} {dependency.Constraint} 不兼容。", "禁用其中一个，或更换作者声明兼容的版本。", !dependency.Warning));
            else if (!dependency.Conflict && matches == false)
                issues.Add(new("前置版本不符：" + dependency.Id, $"{entry.File} 需要 {dependency.Constraint}；当前为 {version}。", dependency.Id == "minecraft" ? "下载适配当前 Minecraft 版本的模组。" : "在模组工作台中更换前置或该模组的版本。", true));
            else if (matches is null)
                issues.Add(new("版本要求需确认：" + dependency.Id, $"{entry.File} 要求 {dependency.Constraint}；当前为 {version ?? "未声明"}。", "该版本表达式或版本号无法可靠比较，请结合作者说明和加载器日志确认。"));
        }
        return issues.DistinctBy(i => (i.Title, i.Evidence)).ToArray();
    }

    private static void ReadArchive(ZipArchive archive, string file, string? loader, List<Entry> output, List<InspectionIssue> issues, CancellationToken ct, int depth)
    {
        var jsonEntry = archive.GetEntry("fabric.mod.json");
        var quiltEntry = archive.GetEntry("quilt.mod.json");
        var forgeEntry = archive.GetEntry("META-INF/mods.toml");
        var neoEntry = archive.GetEntry("META-INF/neoforge.mods.toml");
        var selected = loader switch { "fabric" => jsonEntry, "quilt" => quiltEntry ?? jsonEntry, "forge" => forgeEntry, "neoforge" => neoEntry ?? forgeEntry, _ => neoEntry ?? forgeEntry ?? jsonEntry ?? quiltEntry };
        if (selected is null)
        {
            if (jsonEntry is not null || quiltEntry is not null || forgeEntry is not null || neoEntry is not null)
                issues.Add(new("加载器可能不匹配：" + file, $"当前实例加载器为 {loader ?? "原版"}，文件未包含相应元数据。", "核对项目支持的加载器；兼容桥接模组需要结合作者说明确认。"));
            return;
        }
        var text = Read(selected);
        var nested = new List<string>();
        if (selected.FullName.EndsWith(".json", StringComparison.Ordinal))
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (String(root,"environment") == "server") return;
            var quilt = selected == quiltEntry;
            var scope = quilt && root.TryGetProperty("quilt_loader", out var q) ? q : root;
            var id = String(scope, "id");
            var deps = new List<Dependency>();
            if (scope.TryGetProperty("depends", out var depends))
            {
                if (depends.ValueKind == JsonValueKind.Object)
                    foreach (var dep in depends.EnumerateObject()) deps.Add(new(dep.Name, Constraint(dep.Value)));
                else if (depends.ValueKind == JsonValueKind.Array)
                    foreach (var dep in depends.EnumerateArray())
                        if (dep.ValueKind == JsonValueKind.Object && String(dep, "id") is {} depId && (!dep.TryGetProperty("optional", out var optional) || optional.ValueKind != JsonValueKind.True))
                            deps.Add(new(depId, dep.TryGetProperty("versions", out var v) ? Constraint(v) : "*"));
            }
            foreach (var name in new[] { "breaks", "conflicts" })
                if (root.TryGetProperty(name, out var conflicts) && conflicts.ValueKind == JsonValueKind.Object)
                    foreach (var dep in conflicts.EnumerateObject()) deps.Add(new(dep.Name, Constraint(dep.Value), true, name == "conflicts"));
            if (id is not null) output.Add(new(file, id, String(scope, "version"), deps, depth > 0));
            if (scope.TryGetProperty("provides", out var provides) && provides.ValueKind == JsonValueKind.Array)
                foreach (var alias in provides.EnumerateArray())
                {
                    var name = alias.ValueKind == JsonValueKind.String ? alias.GetString() : alias.ValueKind == JsonValueKind.Object ? String(alias, "id") : null;
                    if (name is not null) output.Add(new(file, name, null, [], true));
                }
            if ((root.TryGetProperty("jars", out var jars) || scope.TryGetProperty("jars",out jars)) && jars.ValueKind == JsonValueKind.Array)
                foreach (var jar in jars.EnumerateArray())
                { var path = jar.ValueKind == JsonValueKind.String ? jar.GetString() : jar.ValueKind == JsonValueKind.Object ? String(jar, "file") : null; if (path is not null) nested.Add(path); }
        }
        else
        {
            var dependencies = new List<Dependency>();
            foreach (Match section in Regex.Matches(text, @"(?ms)^\s*\[\[dependencies\.[^\]]+\]\]\s*(.*?)(?=^\s*\[|\z)"))
            {
                var block = section.Groups[1].Value;
                if (Field(block, "side")?.Equals("SERVER", StringComparison.OrdinalIgnoreCase) == true) continue;
                var type = Field(block, "type");
                var required = type == "required" || Regex.IsMatch(block, @"(?m)^\s*mandatory\s*=\s*true\b");
                if (Field(block, "modId") is {} depId && (required || type == "incompatible"))
                    dependencies.Add(new(depId, Field(block, "versionRange") ?? "*", type == "incompatible"));
            }
            foreach (Match section in Regex.Matches(text, @"(?ms)^\s*\[\[mods\]\]\s*(.*?)(?=^\s*\[|\z)"))
            {
                var block = section.Groups[1].Value;
                if (Field(block, "modId") is {} id) output.Add(new(file, id, Field(block, "version"), dependencies, depth > 0));
            }
        }
        if (archive.GetEntry("META-INF/jarjar/metadata.json") is {} jarJar)
        {
            using var metadata = JsonDocument.Parse(Read(jarJar));
            if (metadata.RootElement.TryGetProperty("jars", out var jars))
                foreach (var item in jars.EnumerateArray()) if (String(item, "path") is {} path) nested.Add(path);
        }
        if (depth >= 4) return;
        foreach (var path in nested.Distinct())
        {
            ct.ThrowIfCancellationRequested();
            if (archive.GetEntry(path) is not {} entry || entry.Length > 33554432) continue;
            using var input = entry.Open(); using var buffer = new MemoryStream(); input.CopyTo(buffer); buffer.Position = 0;
            using var inner = new ZipArchive(buffer, ZipArchiveMode.Read);
            ReadArchive(inner, file, loader, output, issues, ct, depth + 1);
        }
    }

    private static string Read(ZipArchiveEntry entry)
    {
        if (entry.Length > 1048576) throw new InvalidDataException("模组元数据过大。");
        using var input = entry.Open(); using var reader = new StreamReader(input); return reader.ReadToEnd();
    }
    private static string? String(JsonElement value, string key) => value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static string Constraint(JsonElement value) => value.ValueKind switch { JsonValueKind.String => value.GetString() ?? "*", JsonValueKind.Array => string.Join(" || ", value.EnumerateArray().Select(Constraint)), _ => "<复杂版本表达式>" };
    private static string? Field(string text, string key)
    { var m = Regex.Match(text, "(?m)^\\s*" + Regex.Escape(key) + "\\s*=\\s*[\"']([^\"'\\r\\n]*)[\"']"); return m.Success ? m.Groups[1].Value : null; }

    public static bool? Satisfies(string? version, string constraint)
    {
        if (constraint is "*" or "" or "[0,)") return true;
        if (version is null || version.Contains("${", StringComparison.Ordinal)) return null;
        if (constraint.Contains("||", StringComparison.Ordinal))
        { var values = constraint.Split("||").Select(c => Satisfies(version, c.Trim())).ToArray(); return values.Contains(true) ? true : values.Contains(null) ? null : false; }
        if (constraint.StartsWith('[') || constraint.StartsWith('('))
        {
            if (constraint.Length < 3 || constraint[^1] is not (']' or ')')) return null;
            var parts = constraint[1..^1].Split(',');
            if (parts.Length == 1) return Compare(version, parts[0]) is {} exact ? exact == 0 : null;
            if (parts.Length != 2) return null;
            var lower = parts[0].Length == 0 ? 1 : Compare(version, parts[0]);
            var upper = parts[1].Length == 0 ? -1 : Compare(version, parts[1]);
            return lower is null || upper is null ? null : (constraint[0] == '[' ? lower >= 0 : lower > 0) && (constraint[^1] == ']' ? upper <= 0 : upper < 0);
        }
        var tokens = constraint.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length > 1)
        { var values = tokens.Select(c => Satisfies(version, c)).ToArray(); return values.Contains(false) ? false : values.Contains(null) ? null : true; }
        var match = Regex.Match(constraint, @"^(>=|<=|>|<|=|\^|~)?(\d+(?:\.(?:\d+|[xX*]))*)(?:\+[^\s]+)?$");
        if (!match.Success) return version == constraint ? true : null;
        var operation = match.Groups[1].Value; var wanted = match.Groups[2].Value;
        if (wanted.IndexOfAny(['x', 'X', '*']) >= 0)
        { if(operation.Length>0)return null; var prefix = wanted.Split('.').TakeWhile(p => p is not ("x" or "X" or "*")).ToArray(); var actual = version.Split('.'); return actual.Length >= prefix.Length && actual.Take(prefix.Length).SequenceEqual(prefix); }
        var compared = Compare(version, wanted);
        if (compared is null) return null;
        if (operation is "^" or "~")
        {
            var parts = wanted.Split('.').Select(int.Parse).ToList(); var specified=parts.Count; while (parts.Count < 3) parts.Add(0);
            var slot = operation == "~" ? (specified>1 ? 1 : 0) : specified==1||parts[0] > 0 ? 0 : specified==2||parts[1] > 0 ? 1 : 2;
            parts[slot]++; for (var i = slot + 1; i < parts.Count; i++) parts[i] = 0;
            var upper = Compare(version, string.Join('.', parts)); return upper is null ? null : compared >= 0 && upper < 0;
        }
        return operation switch { ">=" => compared >= 0, "<=" => compared <= 0, ">" => compared > 0, "<" => compared < 0, _ => compared == 0 };
    }
    private static int? Compare(string left, string right)
    {
        var a = left.Split('+')[0]; var b = right.Split('+')[0];
        if (!Regex.IsMatch(a, @"^\d+(\.\d+)*$") || !Regex.IsMatch(b, @"^\d+(\.\d+)*$")) return a == b ? 0 : null;
        var aa = a.Split('.'); var bb = b.Split('.');
        for (var i = 0; i < Math.Max(aa.Length, bb.Length); i++)
        {
            if (!int.TryParse(i < aa.Length ? aa[i] : "0", out var x) || !int.TryParse(i < bb.Length ? bb[i] : "0", out var y)) return null;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }
}
