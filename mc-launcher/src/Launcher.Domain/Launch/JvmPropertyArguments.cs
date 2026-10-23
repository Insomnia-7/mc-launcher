namespace Launcher.Domain.Launch;

/// <summary>
/// 编码相关的 <c>-D</c> 属性。中文 Windows 上 JVM 默认跟系统区域走（GBK），
/// 结果是控制台日志、崩溃报告、读中文路径的配置/资源包名全部乱码；
/// 蓝图 §10 坑 9 记的就是这一类。这里一次性把「输出用 UTF-8」钉死。
/// </summary>
/// <remarks>
/// 五个属性分两代：
/// <list type="bullet">
/// <item>Java 18+ 认 <c>stdout.encoding</c> / <c>stderr.encoding</c>（JEP 400 之后的标准写法）；</item>
/// <item>Java 8~17 只认 <c>sun.stdout.encoding</c> / <c>sun.stderr.encoding</c>，那两代属性在新 JDK 上也仍然生效；</item>
/// <item><c>file.encoding</c> 从 Java 18 起默认就是 UTF-8，但 17 及更早跟随区域，所以显式写上。</item>
/// </list>
/// 全写的好处是同一个 argfile 在 Java 8 和 Java 21 上都能拿到 UTF-8 输出，不需要按版本分支。
/// </remarks>
public static class JvmPropertyArguments
{
    /// <summary>固定输出编码的参数，顺序即最终写入 argfile 的顺序。</summary>
    public static readonly IReadOnlyList<string> EncodingArguments =
    [
        "-Dfile.encoding=UTF-8",
        "-Dstdout.encoding=UTF-8",
        "-Dstderr.encoding=UTF-8",
        "-Dsun.stdout.encoding=UTF-8",
        "-Dsun.stderr.encoding=UTF-8",
    ];

    /// <summary>
    /// 取 <c>-D</c> 属性的名字，用来判重：
    /// <c>-Dfile.encoding=GBK</c> → <c>file.encoding</c>；<c>-Dfoo</c>（无值）→ <c>foo</c>；
    /// 不是 <c>-D</c> 参数返回 null。
    /// </summary>
    public static string? PropertyName(string argument)
    {
        if (!argument.StartsWith("-D", StringComparison.Ordinal)) return null;

        var span = argument.AsSpan(2);
        if (span.IsEmpty) return null;

        var equals = span.IndexOf('=');
        if (equals >= 0) span = span[..equals];

        return span.IsEmpty ? null : span.ToString();
    }

    /// <summary>
    /// 过滤掉 <paramref name="declared"/> 里已经出现过的同名 <c>-D</c> 属性。
    /// 语义和 <see cref="JvmPresets.ExcludingDeclared"/> 一致：用户写在「额外 JVM 参数」里的
    /// <c>-Dfile.encoding=GBK</c> 就是比我们的默认值更权威，重复写两条只会让后一条覆盖前一条。
    /// </summary>
    public static List<string> ExcludingDeclared(
        IEnumerable<string> candidates,
        IEnumerable<string> declared)
    {
        HashSet<string>? declaredNames = null;
        foreach (var argument in declared)
        {
            if (PropertyName(argument) is not { } name) continue;
            declaredNames ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            declaredNames.Add(name);
        }

        var result = new List<string>();
        foreach (var argument in candidates)
        {
            // 属性名大小写不敏感（JVM 自身也是这么认的），写错大小写时宁可不追加也不叠两条
            if (declaredNames is not null && PropertyName(argument) is { } name && declaredNames.Contains(name))
                continue;

            result.Add(argument);
        }

        return result;
    }
}
