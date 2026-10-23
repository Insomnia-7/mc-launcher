namespace Launcher.Application.Install;

/// <summary>
/// Windows 长路径（MAX_PATH = 260）安全的路径拼接与文件操作。
/// <para>
/// 整合包里常见 300+ 字符的条目（深层 <c>config/</c>、模组自带的结构化资源目录），
/// 普通 <c>Path.Combine</c> + <c>File.Create</c> 在 Win32 那层会被截断或直接报「路径过长」。
/// 统一做法：相对段自己归一化（顺带挡住 zip slip），绝对路径超过阈值时套 <c>\\?\</c> 前缀，
/// 之后所有 IO 都走本类，不让前缀规则散落到各个调用点。
/// </para>
/// <para>非 Windows 上全部退化成原样透传 —— 那些平台本来就没有 MAX_PATH 这回事。</para>
/// </summary>
public static class LongPath
{
    /// <summary>传统 MAX_PATH：260 个字符。超过它的路径在没加前缀时会被 Win32 拒绝。</summary>
    public const int LegacyMaxPath = 260;

    /// <summary>
    /// 提前到 240 就加前缀：给「前缀之后调用方还要再拼一段」留余量，
    /// 也避开刚好 259/260 时枚举子项越界的那一格。
    /// </summary>
    private const int PrefixThreshold = 240;

    private const string ExtendedPrefix = @"\\?\";
    private const string ExtendedUncPrefix = @"\\?\UNC\";

    private static readonly char[] InvalidNameChars = OperatingSystem.IsWindows()
        ? Path.GetInvalidFileNameChars()
        : ['\0'];

    /// <summary>
    /// 需要时加上 <c>\\?\</c> 前缀。短路径原样返回（不动分隔符），
    /// 相对路径也不加（<c>\\?\</c> 只接受完全限定路径，加了反而非法）。
    /// </summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (!OperatingSystem.IsWindows()) return path;

        var windowsPath = path.Replace('/', '\\');
        if (windowsPath.StartsWith(ExtendedPrefix, StringComparison.Ordinal)) return windowsPath;
        if (windowsPath.Length < PrefixThreshold) return path;
        if (!IsFullyQualified(windowsPath)) return path;

        // UNC（\\server\share\...）必须写成 \\?\UNC\server\share\...，不能直接套 \\?\
        return windowsPath.StartsWith(@"\\", StringComparison.Ordinal)
            ? ExtendedUncPrefix + windowsPath[2..]
            : ExtendedPrefix + windowsPath;
    }

    /// <summary>
    /// 把 <paramref name="relative"/> 归一化后拼到 <paramref name="root"/> 下。
    /// <c>.</c> 丢弃、<c>..</c> 回退、回退出界直接抛 <see cref="InvalidDataException"/>（zip slip 防护）。
    /// 返回的路径不带 <c>\\?\</c> 前缀，需要 IO 时交给 <see cref="CreateFile"/> 等本类方法。
    /// </summary>
    public static string Combine(string root, string relative)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(relative);

        var segments = relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<string>(segments.Length);

        foreach (var segment in segments)
        {
            if (segment == ".") continue;

            if (segment == "..")
            {
                if (stack.Count == 0) throw new InvalidDataException($"整合包内路径越界：{relative}");
                stack.RemoveAt(stack.Count - 1);
                continue;
            }

            if (segment.IndexOfAny(InvalidNameChars) >= 0)
                throw new InvalidDataException($"整合包内路径含非法字符：{relative}");

            stack.Add(segment);
        }

        if (stack.Count == 0) throw new InvalidDataException($"整合包内条目没有有效相对路径：{relative}");

        var trimmed = Path.TrimEndingDirectorySeparator(root);
        return trimmed + Path.DirectorySeparatorChar + string.Join(Path.DirectorySeparatorChar, stack);
    }

    /// <summary>建目录（含中间层），长路径自动加前缀。</summary>
    public static void EnsureDirectory(string path)
    {
        if (string.IsNullOrEmpty(path)) return;

        var target = Normalize(path);
        if (Directory.Exists(target)) return;

        Directory.CreateDirectory(target);
    }

    /// <summary>覆盖写文件；<paramref name="useAsync"/> 默认开启，配合 <c>CopyToAsync</c> 用。</summary>
    public static FileStream CreateFile(string path, bool useAsync = true) =>
        new(Normalize(path), FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync);

    public static bool FileExists(string path) => File.Exists(Normalize(path));

    /// <summary>文件长度；不存在返回 <c>null</c>（下载校验里要区分「没有」和「长度是 0」）。</summary>
    public static long? FileLength(string path)
    {
        var info = new FileInfo(Normalize(path));
        return info.Exists ? info.Length : null;
    }

    public static void DeleteFile(string path)
    {
        var target = Normalize(path);
        if (File.Exists(target)) File.Delete(target);
    }

    /// <summary>递归删目录。长路径下子项也可能超限，所以整棵树都用带前缀的根来枚举 / 删除。</summary>
    public static void DeleteDirectory(string path)
    {
        if (string.IsNullOrEmpty(path)) return;

        var target = Normalize(path);
        if (!Directory.Exists(target)) return;

        Directory.Delete(target, recursive: true);
    }

    /// <summary><c>D:\a\b</c> 或 <c>\\server\share</c> 这类完全限定路径。</summary>
    private static bool IsFullyQualified(string path) =>
        (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\')
        || path.StartsWith(@"\\", StringComparison.Ordinal);
}
