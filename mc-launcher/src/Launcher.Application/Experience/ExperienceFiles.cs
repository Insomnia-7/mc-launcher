using System.Text.Json;

namespace Launcher.Application.Experience;

public static class ExperienceFiles
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static string Child(string root, string relative)
    {
        var basePath = Launcher.Application.Install.LongPath.Canonical(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new InvalidDataException("需要有效的相对路径。");
        var path = Path.GetFullPath(Path.Combine(basePath, relative));
        if (!path.StartsWith(basePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("文件路径超出指定目录。");
        return path;
    }
    public static void RejectLinks(string path)
    {
        for (string? p = Launcher.Application.Install.LongPath.Canonical(path); p is not null; p = Path.GetDirectoryName(p))
            if ((Directory.Exists(p) || File.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("请先移除目录或文件链接：" + p);
    }
    public static T? Read<T>(string path)
    {
        try{return File.Exists(path)?JsonSerializer.Deserialize<T>(File.ReadAllText(path),Json):default;}
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or JsonException){return default;}
    }
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static IEnumerable<string> Files(string root)
    {
        if (!Directory.Exists(root)) yield break;
        RejectLinks(root);
        foreach (var file in Directory.EnumerateFiles(root)) { RejectLinks(file); yield return file; }
        foreach (var directory in Directory.EnumerateDirectories(root))
            foreach (var file in Files(directory)) yield return file;
    }
    public static void DeleteTree(string path, string ownerRoot)
    {
        var relative = Path.GetRelativePath(Launcher.Application.Install.LongPath.Canonical(ownerRoot), Launcher.Application.Install.LongPath.Canonical(path));
        var safe = Child(ownerRoot, relative);
        RejectLinks(safe);
        Launcher.Application.Install.LongPath.DeleteDirectory(safe);
    }
}
