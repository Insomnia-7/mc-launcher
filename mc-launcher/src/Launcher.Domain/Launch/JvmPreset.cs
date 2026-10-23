namespace Launcher.Domain.Launch;

/// <summary>
/// JVM 参数档位。只描述「追加哪些 -XX」，不碰 -Xmx/-Xms（内存由 <see cref="LaunchContext.MemoryMb"/>
/// 决定），也不碰版本 JSON 里的 -D 参数——那两样是正确性的一部分，不是调优旋钮。
/// </summary>
public enum JvmPreset
{
    /// <summary>不加任何 GC 参数，完全保持历史行为。默认值必须是 0：settings.json 里没写这个字段的老实例按它读。</summary>
    Default = 0,

    /// <summary>追求启动速度：G1 + 并发处理引用 + AlwaysPreTouch + 关掉 hsperfdata 共享内存写入。</summary>
    Fast = 1,

    /// <summary>保守档：只显式打开 G1，其余交给 JVM 默认值。给「加了参数反而更慢/崩」的机器用。</summary>
    Compatible = 2,
}

/// <summary>
/// 档位 → 具体参数，以及「用户/版本 JSON 已经声明过同名 -XX 就不要再追加」的判重规则。
/// 放在 Domain 是因为 <see cref="LaunchPlanBuilder"/> 是纯计算，不能回头问 Application 要配置。
/// </summary>
public static class JvmPresets
{
    /// <summary>
    /// 打开实验性 VM 选项。Fast 档里的 <c>-XX:G1NewSizePercent</c> 在 JDK 11+ 属于 experimental，
    /// 不先解锁 JDK 会直接拒绝启动（实测 Zulu 21.0.1：
    /// <c>Error: VM option 'G1NewSizePercent' is experimental and must be enabled via -XX:+UnlockExperimentalVMOptions</c>）。
    /// 它只是「解锁开关」，不是调优值，所以判重时永远保留，绝不让位给用户参数——
    /// 否则用户自己也声明了一次稍后位置的同名参数，去重会把靠前的那条删掉，实验项又变回「未解锁」。
    /// </summary>
    public const string UnlockExperimentalOptions = "-XX:+UnlockExperimentalVMOptions";

    /// <summary>
    /// Fast 档的参数与顺序。顺序固定，argfile 里读起来和日志比对都方便。
    /// 第一条必须是解锁开关：它得排在 <c>G1NewSizePercent</c> 前面，JVM 是按出现顺序校验的。
    /// </summary>
    private static readonly string[] FastArguments =
    [
        UnlockExperimentalOptions,
        "-XX:+UseG1GC",
        "-XX:MaxGCPauseMillis=50",
        "-XX:G1NewSizePercent=20",
        "-XX:G1ReservePercent=20",
        "-XX:MaxTenuringThreshold=1",
        "-XX:+AlwaysPreTouch",
        "-XX:+ParallelRefProcEnabled",
        "-XX:+PerfDisableSharedMem",
    ];

    private static readonly string[] CompatibleArguments =
    [
        "-XX:+UseG1GC",
    ];

    private static readonly string[] NoArguments = [];

    /// <summary>档位对应的参数列表（只读、共享，调用方不要就地修改）。</summary>
    public static IReadOnlyList<string> ArgumentsFor(JvmPreset preset) => preset switch
    {
        JvmPreset.Fast => FastArguments,
        JvmPreset.Compatible => CompatibleArguments,
        _ => NoArguments,
    };

    /// <summary>settings.json / 命令行里用的稳定短名。</summary>
    public static string Key(JvmPreset preset) => preset switch
    {
        JvmPreset.Fast => "fast",
        JvmPreset.Compatible => "compatible",
        _ => "default",
    };

    /// <summary>
    /// 把越界的枚举值（手改 settings.json 写成 99）收敛回 Default。
    /// 放在这里而不是调用方：枚举的取值范围只有本文件知道，<c>Enum.IsDefined</c> 写一次就够了。
    /// </summary>
    public static JvmPreset Normalize(JvmPreset preset) =>
        Enum.IsDefined(preset) ? preset : JvmPreset.Default;

    /// <summary>解析 "fast" / "compatible" / "default"（大小写不敏感），认不出来返回 false。</summary>
    public static bool TryParse(string? text, out JvmPreset preset)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "fast":
                preset = JvmPreset.Fast;
                return true;
            case "compatible":
                preset = JvmPreset.Compatible;
                return true;
            case "default" or "" or null:
                preset = JvmPreset.Default;
                return true;
            default:
                preset = JvmPreset.Default;
                return false;
        }
    }

    /// <summary>
    /// 取 -XX: 选项的逻辑名，用来判重：
    /// <c>-XX:+UseG1GC</c> / <c>-XX:-UseG1GC</c> / <c>-XX:MaxGCPauseMillis=50</c>
    /// → <c>UseG1GC</c> / <c>UseG1GC</c> / <c>MaxGCPauseMillis</c>。非 -XX 参数返回 null。
    /// </summary>
    public static string? OptionName(string argument)
    {
        if (!argument.StartsWith("-XX:", StringComparison.Ordinal)) return null;

        var span = argument.AsSpan(4);
        if (span.Length == 0) return null;
        if (span[0] is '+' or '-') span = span[1..];

        var equals = span.IndexOf('=');
        if (equals >= 0) span = span[..equals];

        return span.IsEmpty ? null : span.ToString();
    }

    /// <summary>
    /// 过滤掉 <paramref name="declared"/> 里已经出现过的同名 -XX 参数。
    /// 「以用户为准」的实际含义就在这里：用户的 ExtraJvmArgs 和版本 JSON 的 jvm 段都算已声明，
    /// 预设只补空缺，绝不覆盖别人的设置。
    /// </summary>
    public static List<string> ExcludingDeclared(
        IEnumerable<string> presetArguments,
        IEnumerable<string> declared)
    {
        HashSet<string>? declaredNames = null;
        foreach (var argument in declared)
        {
            if (OptionName(argument) is not { } name) continue;
            declaredNames ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            declaredNames.Add(name);
        }

        var result = new List<string>();
        foreach (var argument in presetArguments)
        {
            // 解锁开关不参与判重：重复一条 -XX:+UnlockExperimentalVMOptions 只是布尔量再置一次，无害；
            // 少一条却会让紧跟其后的实验项直接启动失败。
            if (string.Equals(argument, UnlockExperimentalOptions, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(argument);
                continue;
            }

            // -XX 名字比较大小写不敏感：真出现大小写写错的情况，宁可不重复也不叠两条
            if (declaredNames is not null && OptionName(argument) is { } name && declaredNames.Contains(name))
                continue;

            result.Add(argument);
        }

        return result;
    }
}
