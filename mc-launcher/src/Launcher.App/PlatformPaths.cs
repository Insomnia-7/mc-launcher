using System.IO;
using Serilog;
namespace Launcher.App;

/// <summary>Windows 侧路径约定（ADR-001：平台差异只允许出现在 App/Infrastructure.Windows）。</summary>
public static class AppPaths
{
    /// <summary>
    /// 数据根目录。优先级：MCLAUNCHER_HOME &gt; %APPDATA%\MCLauncher。
    /// 可通过环境变量覆盖默认数据目录。
    /// </summary>
    public static string DataDirectory { get; } =
        Environment.GetEnvironmentVariable("MCLAUNCHER_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MCLauncher");

    public static string LogsDirectory { get; } = Path.Combine(DataDirectory, "logs");
    public static string InstancesDirectory { get; } = Path.Combine(DataDirectory, "instances");
    public static string CacheDirectory { get; } = Path.Combine(DataDirectory, "cache");

    /// <summary>
    /// 游戏目录。优先级：MCLAUNCHER_GAME_ROOT &gt; 数据目录下的 game-root.txt &gt; 数据目录\game。
    /// 界面上「选择游戏目录…」改的就是 game-root.txt，改完立即生效、下次启动仍然生效。
    /// </summary>
    public static string GameRoot { get; private set; } = ResolveGameRoot();

    public static string GameRootOverrideFile => Path.Combine(DataDirectory, "game-root.txt");

    /// <summary>
    /// 上次记住的游戏目录这次不可用时，这里放一句给用户看的中文说明（正常时是 null）。
    /// 界面上会把它当作「游戏目录有问题」的提示，并且目录已经回退到默认位置。
    /// </summary>
    public static string? GameRootFallbackNotice { get; private set; }

    private static string ResolveGameRoot()
    {
        if (Environment.GetEnvironmentVariable("MCLAUNCHER_GAME_ROOT") is { Length: > 0 } custom)
            return custom;

        var fallback = Path.Combine(DataDirectory, "game");
        string? saved = null;
        try
        {
            if (File.Exists(GameRootOverrideFile))
                saved = File.ReadAllText(GameRootOverrideFile).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 记不下来就用默认目录，别为了一个路径文件起不来
            Serilog.Log.Warning(ex, "读取游戏目录设置失败：{File}", GameRootOverrideFile);
        }

        if (string.IsNullOrEmpty(saved)) return fallback;

        // 上次选的目录这次用不了（U 盘拔了、盘符变了、路径被文件占住）：不能每次都把
        // 用户卡在一个空列表上，先回退到默认目录，把原因留在界面上，文件另存一份不直接删。
        var problem = Launcher.Application.Diagnostics.GameRootProbe.Describe(saved);
        if (problem is null) return saved;

        GameRootFallbackNotice = $"上次用的游戏目录现在打不开：{saved}\n{problem}\n已临时改用默认目录 {fallback}；可以用下面的按钮重新选一个。";
        Serilog.Log.Warning("记住的游戏目录不可用，回退到默认目录：{Saved} -> {Fallback}", saved, fallback);
        try
        {
            File.Move(GameRootOverrideFile, Path.Combine(DataDirectory, "game-root.broken.txt"), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Warning(ex, "备份失效的游戏目录设置失败：{File}", GameRootOverrideFile);
        }
        return fallback;
    }

    /// <summary>把用户选的目录记下来并立即生效；环境变量优先的情况下返回 false。</summary>
    public static bool TrySetGameRoot(string path)
    {
        if (Environment.GetEnvironmentVariable("MCLAUNCHER_GAME_ROOT") is { Length: > 0 }) return false;

        GameRoot = path;
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(GameRootOverrideFile, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Warning(ex, "保存游戏目录设置失败：{File}", GameRootOverrideFile);
        }
        return true;
    }

    /// <summary>
    /// 建好启动器自己要用的目录。游戏目录可能是用户随手填的坏路径（被同名文件占住、
    /// 只读盘、父目录建不出来），这一类失败不能让启动器起不来——记下来交给界面提示。
    /// </summary>
    public static void EnsureCreated()
    {
        CreateQuietly(LogsDirectory);
        CreateQuietly(InstancesDirectory);
        CreateQuietly(CacheDirectory);
        CreateQuietly(GameRoot);
    }

    private static void CreateQuietly(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // 具体原因由 GameRootProbe 在视图里转成中文提示，这里只保证不炸
            Log.Warning(ex, "创建目录失败：{Path}", path);
        }
    }
}

public static class BuildInfo
{
    public const string Name = "MCLauncher";
    public static string Version { get; } =
        typeof(BuildInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
