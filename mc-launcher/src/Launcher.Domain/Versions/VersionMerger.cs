namespace Launcher.Domain.Versions;

/// <summary>
/// inheritsFrom 合并。Fabric/Quilt 的 profile、Forge 生成的增量 json 都只写差异，
/// 直接拿去启动会因为缺 libraries/mainClass 而炸 —— 必须与父版本合并。
/// </summary>
public static class VersionMerger
{
    public static VersionJson Merge(VersionJson child, VersionJson parent) => new()
    {
        Id = child.Id,
        Type = child.Type ?? parent.Type,
        MainClass = child.MainClass ?? parent.MainClass,
        InheritsFrom = null,
        Assets = child.Assets ?? parent.Assets,
        AssetIndex = child.AssetIndex ?? parent.AssetIndex,
        Downloads = parent.Downloads,                                   // client.jar 永远来自父版本
        JavaVersion = child.JavaVersion ?? parent.JavaVersion,
        Libraries = MergeLibraries(parent.Libraries, child.Libraries),
        Arguments = MergeArguments(parent.Arguments, child.Arguments),
        MinecraftArguments = child.MinecraftArguments ?? parent.MinecraftArguments,
        ComplianceLevel = child.ComplianceLevel ?? parent.ComplianceLevel,
        ReleaseTime = child.ReleaseTime ?? parent.ReleaseTime,
        Time = child.Time ?? parent.Time,
        LauncherParentId = child.LauncherParentId ?? parent.Id,
    };

    /// <summary>
    /// 按 group:artifact[:classifier] 去重，子版本同坐标的库直接顶掉父版本。
    /// 不做这件事，Fabric 会因为父版本自带 asm 9.6 + 自己带 9.10.1 而报
    /// "duplicate ASM classes found on classpath" 直接拒绝启动。
    /// </summary>
    private static List<Library> MergeLibraries(List<Library> parent, List<Library> child)
    {
        var childKeys = child.Select(CoordinateKey).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = new List<Library>(parent.Count + child.Count);
        result.AddRange(parent.Where(library => !childKeys.Contains(CoordinateKey(library))));
        result.AddRange(child);
        return result;
    }

    private static string CoordinateKey(Library library)
    {
        var parts = library.Name.Split(':');
        var classifier = parts.Length > 3 ? ":" + parts[3] : "";
        return $"{library.GroupId}:{library.ArtifactId}{classifier}";
    }

    private static Arguments? MergeArguments(Arguments? parent, Arguments? child)
    {
        if (parent is null && child is null) return null;

        return new Arguments
        {
            Game = [.. parent?.Game ?? [], .. child?.Game ?? []],
            Jvm = [.. parent?.Jvm ?? [], .. child?.Jvm ?? []],
        };
    }
}
