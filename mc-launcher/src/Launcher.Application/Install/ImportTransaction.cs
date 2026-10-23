namespace Launcher.Application.Install;

/// <summary>
/// 导入事务：import 开跑前扫一眼 <c>versions/</c> 与 <c>instances/</c> 里已经有什么，
/// 失败回滚时只删这段期间<b>新出现</b>的目录。
/// <para>
/// 三条硬规矩：
/// ① 原本就存在的版本 / 实例目录一律不碰（用户装过的 1.20.1 不能被一次失败的导入带走）；
/// ② <c>libraries/</c> 与 <c>assets/</c> 压根不在扫描范围里 —— 它们跨实例共享，
///    删掉等于把别人的游戏也删了，代价远大于留几百 MB 垃圾；
/// ③ 只处理这两个根目录的直接子目录，不递归猜别的位置。
/// </para>
/// <para>
/// 用「快照 + 差集」而不是「记下我知道的 id」：Forge / NeoForge installer 失败时版本 id
/// 还在它自己的 <c>install_profile.json</c> 里，调用方根本不知道它建了什么目录。
/// </para>
/// </summary>
public sealed class ImportTransaction
{
    private const string VersionsDirectory = "versions";
    private const string InstancesDirectory = "instances";

    private readonly string _gameRoot;
    private readonly HashSet<string>? _versionsBefore;
    private readonly HashSet<string>? _instancesBefore;
    private readonly List<string> _removed = [];

    private ImportTransaction(string gameRoot, HashSet<string>? versionsBefore, HashSet<string>? instancesBefore)
    {
        _gameRoot = gameRoot;
        _versionsBefore = versionsBefore;
        _instancesBefore = instancesBefore;
    }

    /// <summary>回滚时删掉的目录（绝对路径），按删除顺序。</summary>
    public IReadOnlyList<string> RemovedPaths => _removed;

    /// <summary>记下 <c>versions/</c> 与 <c>instances/</c> 的现状。必须在第一条写入之前调用。</summary>
    public static ImportTransaction Begin(string gameRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRoot);

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameRoot));
        return new ImportTransaction(root, Snapshot(root, VersionsDirectory), Snapshot(root, InstancesDirectory));
    }

    /// <summary>
    /// 删掉本次新建的版本 / 实例目录，返回删掉的路径。
    /// 删不掉的目录只跳过（不覆盖原始异常）—— 回滚失败也得把真正的失败原因抛给用户。
    /// </summary>
    public IReadOnlyList<string> Rollback()
    {
        // 实例目录引用版本目录，先删外层再删版本，语义上更顺
        DeleteNewDirectories(InstancesDirectory, _instancesBefore);
        DeleteNewDirectories(VersionsDirectory, _versionsBefore);
        return _removed;
    }

    private void DeleteNewDirectories(string directoryName, HashSet<string>? before)
    {
        if (before is null) return; // 无法建立快照时保留该根目录，不推断目录归属。
        var root = Path.Combine(_gameRoot, directoryName);
        if (!Directory.Exists(root)) return;

        string[] children;
        try
        {
            children = Directory.GetDirectories(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // 扫不动就什么都不删，比误删安全
        }

        // 路径长的先删没意义（都是直接子目录），按名字排序只为让报告稳定
        foreach (var child in children.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            if (before.Contains(Path.GetFileName(child))) continue;

            try
            {
                LongPath.DeleteDirectory(child);
                _removed.Add(child);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 独占句柄 / 权限不足：跳过这一项，别让回滚失败盖掉原始异常
            }
        }
    }

    private static HashSet<string>? Snapshot(string root, string directoryName)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(root, directoryName);

        try
        {
            foreach (var child in Directory.GetDirectories(path))
                names.Add(Path.GetFileName(child));
        }
        catch (DirectoryNotFoundException) { return names; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // 不可读取与空目录不能混为一谈。
        }

        return names;
    }
}
