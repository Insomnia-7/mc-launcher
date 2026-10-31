using Launcher.Domain.Versions;

namespace Launcher.Domain.Launch;

/// <summary>
/// 把 版本 JSON + 启动上下文 折算成最终启动计划。纯计算：不碰网络、不起进程。
/// 这是 M1 的核心，规则错了会表现为"参数看着对但 ClassNotFound / 找不到资源"。
/// </summary>
public static class LaunchPlanBuilder
{
    public static LaunchPlan Build(
        VersionJson version,
        PlatformContext ctx,
        LaunchContext launch,
        string javaPath, ClassPathResult? preparedClassPath = null)
    {
        if (string.IsNullOrWhiteSpace(version.MainClass))
            throw new InvalidOperationException($"版本 {version.Id} 没有 mainClass，无法构建启动命令");

        var classPath = preparedClassPath ?? ClassPathBuilder.Build(version, ctx, launch.LibrariesRoot, launch.ClientJarPath);
        var nativesDirectory = ResolveNativesDirectory(launch);

        var variables = BuildVariables(version, ctx, launch, classPath, nativesDirectory);

        var jvm = BuildJvmArguments(version, ctx, variables, launch);
        var game = BuildGameArguments(version, ctx, variables, launch);

        return new LaunchPlan(
            JavaPath: javaPath,
            MainClass: version.MainClass,
            JvmArguments: MoveClassPathToEnd(jvm),
            GameArguments: game,
            WorkingDirectory: launch.GameDirectory);
    }

    public static IReadOnlyDictionary<string, string> BuildVariables(
        VersionJson version,
        PlatformContext ctx,
        LaunchContext launch,
        ClassPathResult classPath,
        string nativesDirectory)
    {
        var assetsIndexName = version.Assets ?? version.AssetIndex?.Id ?? "legacy";

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_player_name"] = launch.PlayerName,
            ["auth_uuid"] = launch.Uuid.Replace("-", "", StringComparison.Ordinal),
            ["auth_access_token"] = launch.AccessToken ?? "0",
            ["auth_session"] = launch.AccessToken ?? "0",       // ≤1.6 的老占位符
            ["auth_xuid"] = "",
            ["clientid"] = "",
            ["user_type"] = launch.UserType,
            ["version_name"] = version.Id,                       // Forge 靠它定位 libraries 目录，不能乱填
            ["version_type"] = version.Type ?? "release",
            ["game_directory"] = launch.GameDirectory,
            ["assets_root"] = launch.AssetsRoot,
            ["game_assets"] = Path.Combine(launch.AssetsRoot, "virtual", "legacy"),  // ≤1.7.2
            ["assets_index_name"] = assetsIndexName,
            ["natives_directory"] = nativesDirectory,
            ["classpath"] = classPath.ToClassPathString(Path.PathSeparator),
            ["launcher_name"] = launch.LauncherName,
            ["launcher_version"] = launch.LauncherVersion,
            ["library_directory"] = launch.LibrariesRoot,        // Forge 1.17+ 新占位符
            ["classpath_separator"] = Path.PathSeparator.ToString(),
            ["resolution_width"] = launch.WindowWidth.ToString(),
            ["resolution_height"] = launch.WindowHeight.ToString(),
        };
    }

    private static List<string> BuildJvmArguments(
        VersionJson version,
        PlatformContext ctx,
        IReadOnlyDictionary<string, string> variables,
        LaunchContext launch)
    {
        var result = new List<string>
        {
            $"-Xmx{launch.MemoryMb}M",
            $"-Xms{Math.Min(launch.MemoryMb, 1024)}M",
        };

        result.AddRange(ArgumentExpander.SplitArguments(launch.ExtraJvmArgs));
        if(ctx.OsName=="osx")result.Add("-XstartOnFirstThread");

        // 1.13+ 有 arguments.jvm；更早的版本只有隐含约定
        if (version.Arguments is { Jvm.Count: > 0 })
        {
            result.AddRange(ArgumentExpander.ExpandAll(version.Arguments.Jvm, ctx, variables));
        }
        else
        {
            result.Add(ArgumentExpander.Expand("-Djava.library.path=${natives_directory}", variables));
            result.Add("-cp");
            result.Add(ArgumentExpander.Expand("${classpath}", variables));
        }

        // 兜底：老版本 JSON 里不写 -Djava.library.path 的情况
        if (!result.Any(a => a.StartsWith("-Djava.library.path=", StringComparison.Ordinal)))
            result.Insert(0, "-Djava.library.path=" + variables["natives_directory"]);

        // 编码属性统一走 JvmPropertyArguments：五个 -D 一次补齐，用户/版本 JSON 已经声明过的同名属性跳过。
        // Java 17 及更早默认跟随系统区域（中文 Windows 就是 GBK），读中文路径的配置/资源包名会乱码；
        // Java 18+ 的 stdout.encoding 让重定向到文件的日志也是 UTF-8（蓝图 §10 坑 9）。
        result.AddRange(JvmPropertyArguments.ExcludingDeclared(JvmPropertyArguments.EncodingArguments, result));

        InsertPresetArguments(result, launch.JvmPreset);

        return result;
    }

    /// <summary>
    /// natives 落盘位置。<see cref="LaunchContext.NativesDirectory"/> 优先——调用方可以先用
    /// <c>NativesProvisioner</c> 解压到 <c>&lt;gameRoot&gt;/natives/&lt;versionId&gt;/&lt;指纹&gt;</c> 再传进来，
    /// 这样进程里拿到的 <c>-Djava.library.path</c> 和实际解压目录永远是同一个。
    /// 没传就退回历史位置 <c>versions/&lt;id&gt;/natives</c>，保持老调用方的行为不变。
    /// </summary>
    public static string ResolveNativesDirectory(LaunchContext launch)
    {
        if (!string.IsNullOrWhiteSpace(launch.NativesDirectory)) return launch.NativesDirectory;

        var versionDirectory = Path.GetDirectoryName(launch.ClientJarPath) ?? launch.GameDirectory;
        return Path.Combine(versionDirectory, "natives");
    }

    /// <summary>
    /// 把档位参数插到 -Xms 之后（紧跟在内存开关后面，和官方启动器/主流启动器的排版一致），
    /// 已经由用户 ExtraJvmArgs 或版本 JSON 声明过的同名 -XX 一律跳过。
    /// </summary>
    private static void InsertPresetArguments(List<string> jvmArguments, JvmPreset preset)
    {
        var presetArguments = JvmPresets.ArgumentsFor(preset);
        if (presetArguments.Count == 0) return;

        var addition = JvmPresets.ExcludingDeclared(presetArguments, jvmArguments);
        if (addition.Count == 0) return;

        // -Xmx 之后是 -Xms（固定两行），预设永远排在它们后面，不会把内存开关挤走
        var insertAt = Math.Min(2, jvmArguments.Count);
        jvmArguments.InsertRange(insertAt, addition);
    }

    private static List<string> BuildGameArguments(
        VersionJson version,
        PlatformContext ctx,
        IReadOnlyDictionary<string, string> variables,
        LaunchContext launch)
    {
        var result = new List<string>();

        if (version.Arguments is { Game.Count: > 0 })
        {
            result.AddRange(ArgumentExpander.ExpandAll(version.Arguments.Game, ctx, variables));
        }
        else if (!string.IsNullOrWhiteSpace(version.MinecraftArguments))
        {
            foreach (var raw in ArgumentExpander.SplitArguments(version.MinecraftArguments))
                result.Add(ArgumentExpander.Expand(raw, variables));
        }

        if (ctx.HasCustomResolution)
        {
            result.Add("--width");
            result.Add(launch.WindowWidth.ToString());
            result.Add("--height");
            result.Add(launch.WindowHeight.ToString());
        }

        if (launch.FullScreen) result.Add("--fullscreen");

        result.AddRange(ArgumentExpander.SplitArguments(launch.ExtraGameArgs));

        return result;
    }

    /// <summary>-cp 挪到 main class 前面最后一个位置，日志和 argfile 都好读。</summary>
    private static List<string> MoveClassPathToEnd(List<string> jvmArguments)
    {
        var index = jvmArguments.FindIndex(a => a is "-cp" or "-classpath");
        if (index < 0 || index + 1 >= jvmArguments.Count) return jvmArguments;

        var value = jvmArguments[index + 1];
        var result = new List<string>(jvmArguments.Count);
        result.AddRange(jvmArguments.Take(index));
        result.AddRange(jvmArguments.Skip(index + 2));
        result.Add(jvmArguments[index]);
        result.Add(value);
        return result;
    }
}
