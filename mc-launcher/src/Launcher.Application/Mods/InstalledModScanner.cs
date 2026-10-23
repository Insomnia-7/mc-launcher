namespace Launcher.Application.Mods;

/// <summary>mods 目录里的一行。元数据读不出来时 <see cref="ModId"/> 为 null，其余字段尽量给。</summary>
public sealed record InstalledMod(
    string FileName,
    string FullPath,
    long Bytes,
    bool Enabled,
    string? ModId,
    string? Name,
    string? Version,
    string? LoaderKind)
{
    /// <summary>人话：「JEI 15.2.0.27（forge）」，缺元数据时退回文件名。</summary>
    public string Describe()
    {
        var name = Name ?? FileName;
        var version = string.IsNullOrWhiteSpace(Version) ? "" : $" {Version}";
        var loader = string.IsNullOrWhiteSpace(LoaderKind) ? "" : $"（{LoaderKind}）";
        return $"{name}{version}{loader}";
    }
}

/// <summary>一次扫描的结果。全部是纯派生量，不缓存、不落盘。</summary>
public sealed record InstalledModScan(IReadOnlyList<InstalledMod> Mods)
{
    public int EnabledCount => Mods.Count(m => m.Enabled);

    public int DisabledCount => Mods.Count(m => !m.Enabled);

    public long TotalBytes => Mods.Sum(m => m.Bytes);

    public double TotalMegabytes => TotalBytes / 1024d / 1024d;

    /// <summary>
    /// 同一个 modId 出现在两个以上文件里的「mod 个数」。典型场景：整合包自带旧版 + 用户后下的新版，
    /// Forge 会直接抛 duplicate mod。含 .disabled 文件——那只是被临时关掉，重新启用就是真冲突。
    /// 没有重复、或所有文件都读不出 modId 时为 0。
    /// </summary>
    public int? DuplicateModIdCount => DuplicateModIds.Count;

    /// <summary>重复的 modId 明细，界面可以照着提示用户删哪一个。</summary>
    public IReadOnlyList<string> DuplicateModIds => Mods
        .Where(m => !string.IsNullOrEmpty(m.ModId))
        .GroupBy(m => m.ModId!, StringComparer.Ordinal)
        .Where(g => g.Count() > 1)
        .Select(g => g.Key)
        .OrderBy(id => id, StringComparer.Ordinal)
        .ToList();
}

/// <summary>
/// 扫一个 mods 目录：文件名、大小、启用状态，外加从 jar 里读出来的 modId / 名字 / 版本 / 加载器。
/// 和 <see cref="ModFileGuard"/> 共用同一套元数据来源与缓存，判重与扫描不会给出两套结论。
/// 坏包不抛，标成 <c>ModId=null</c> 继续扫下一个。
/// </summary>
public static class InstalledModScanner
{
    /// <summary>被禁用的 mod 统一是 <c>xxx.jar.disabled</c>。</summary>
    private const string DisabledSuffix = ".disabled";

    private const string JarSuffix = ".jar";

    public static InstalledModScan Scan(string modsDirectory, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(modsDirectory) || !Directory.Exists(modsDirectory))
        {
            return new InstalledModScan([]);
        }

        var mods = new List<InstalledMod>();

        foreach (var path in Directory.EnumerateFiles(modsDirectory).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();

            var fileName = Path.GetFileName(path);
            if (!IsModFile(fileName))
            {
                continue;
            }

            var metadata = ModFileGuard.ReadMetadata(path);

            mods.Add(new InstalledMod(
                FileName: fileName,
                FullPath: path,
                Bytes: ReadSize(path),
                Enabled: !fileName.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase),
                ModId: metadata?.PrimaryModId,
                Name: string.IsNullOrWhiteSpace(metadata?.DisplayName) ? Stem(fileName) : metadata!.DisplayName,
                Version: string.IsNullOrWhiteSpace(metadata?.Version) ? null : metadata!.Version,
                LoaderKind: metadata?.LoaderKind));
        }

        mods.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.FileName, right.FileName));
        return new InstalledModScan(mods);
    }

    /// <summary>mods 目录里只挑 jar 与 jar.disabled，别的文件（.txt、.toml、下载残留）不列。</summary>
    private static bool IsModFile(string fileName) =>
        fileName.EndsWith(JarSuffix, StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(JarSuffix + DisabledSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>去掉 .disabled 与扩展名，作为没有元数据时的显示名。</summary>
    private static string Stem(string fileName)
    {
        var name = fileName;
        if (name.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^DisabledSuffix.Length];
        }

        var stem = Path.GetFileNameWithoutExtension(name);
        return string.IsNullOrWhiteSpace(stem) ? fileName : stem;
    }

    private static long ReadSize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
