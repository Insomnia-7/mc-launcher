using System.IO;
using System.Reflection;
using Serilog;
namespace Launcher.App;

/// <summary>Windows 侧路径约定（ADR-001：平台差异只允许出现在 App/Infrastructure.Windows）。</summary>
public static class AppPaths
{
    /// <summary>
    /// 数据根目录：环境覆盖、便携位置记录、既有用户位置、新用户程序旁目录。
    /// 可通过环境变量覆盖默认数据目录。
    /// </summary>
    public static string DataRootSettingsFile => Path.Combine(AppContext.BaseDirectory, "fangyu-data-root.txt");
    public static string DataDirectory { get; } = ResolveDataDirectory();
    private static string ResolveDataDirectory()
    {
        if (Environment.GetEnvironmentVariable("MCLAUNCHER_HOME") is { Length: > 0 } home) return Path.GetFullPath(home);
        if (File.Exists(DataRootSettingsFile)) return Path.GetFullPath(File.ReadAllText(DataRootSettingsFile).Trim());
        var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MCLauncher");
        // Preserve existing users. A fresh portable download keeps its data beside the EXE.
        if (Directory.Exists(legacy) && Directory.EnumerateFileSystemEntries(legacy).Any()) return legacy;
        return OperatingSystem.IsMacOS() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Fangyu") : Path.Combine(AppContext.BaseDirectory, "FangyuData");
    }
    public static bool IsGameRootConfirmed => Environment.GetEnvironmentVariable("MCLAUNCHER_GAME_ROOT") is { Length: > 0 } || SavedGameRootReadable;
    private static bool SavedGameRootReadable;

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
    /// 保留原目录，不自动另建游戏库。
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
            SavedGameRootReadable = !string.IsNullOrWhiteSpace(saved);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 记不下来就用默认目录，别为了一个路径文件起不来
            Serilog.Log.Warning(ex, "读取游戏目录设置失败：{File}", GameRootOverrideFile);
        }

        if (string.IsNullOrEmpty(saved)) return fallback;

        if (!Directory.Exists(saved)) GameRootFallbackNotice = $"游戏目录暂不可用：{saved}。请重新连接磁盘或选择目录；不会自动切换到其他位置。";
        return saved;
    }

    /// <summary>把用户选的目录记下来并立即生效；环境变量优先的情况下返回 false。</summary>
    public static bool TrySetGameRoot(string path)
    {
        if (Environment.GetEnvironmentVariable("MCLAUNCHER_GAME_ROOT") is { Length: > 0 } configured) return string.Equals(Path.GetFullPath(configured),Path.GetFullPath(path),OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal);

        var full = Path.GetFullPath(path);
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var temp = GameRootOverrideFile + ".tmp";
            File.WriteAllText(temp, full);
            File.Move(temp, GameRootOverrideFile, true);
            GameRoot = full; SavedGameRootReadable = true; GameRootFallbackNotice = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "保存游戏目录设置失败：{File}", GameRootOverrideFile);
            return false;
        }
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
        // Game libraries are created only after an explicit location choice.
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
        typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(BuildInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
