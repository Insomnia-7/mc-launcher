namespace Launcher.Application.Settings;

/// <summary>
/// 数据目录约定：<c>MCLAUNCHER_HOME</c> &gt; <c>%APPDATA%\MCLauncher</c>。
/// WPF 侧 AppPaths 有同样的逻辑（那里还要算 logs/instances），这里给 Core 和 CLI 用，
/// 避免 settings.json 的位置在多处各写一份、慢慢漂掉。
/// </summary>
public static class LauncherDataPaths
{
    public const string HomeEnvironmentVariable = "MCLAUNCHER_HOME";
    public const string DefaultDirectoryName = "MCLauncher";
    public const string SettingsFileName = "settings.json";

    /// <summary>数据目录绝对路径，不保证存在。</summary>
    public static string ResolveHome()
    {
        if (Environment.GetEnvironmentVariable(HomeEnvironmentVariable) is { Length: > 0 } home &&
            home.Trim() is { Length: > 0 } trimmed)
        {
            return trimmed;
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, DefaultDirectoryName);
    }

    /// <summary>settings.json 的完整路径。传了 dataDirectory 就用它，否则走 <see cref="ResolveHome"/>。</summary>
    public static string ResolveSettingsFile(string? dataDirectory = null) =>
        Path.Combine(string.IsNullOrWhiteSpace(dataDirectory) ? ResolveHome() : dataDirectory, SettingsFileName);
}
