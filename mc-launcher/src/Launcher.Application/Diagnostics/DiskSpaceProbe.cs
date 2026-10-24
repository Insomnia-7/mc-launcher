using System;
using System.Globalization;
using System.IO;

namespace Launcher.Application.Diagnostics;

/// <summary>
/// 下载 / 安装前问一句盘上还有没有地方。
/// 一个原版版本落地大约 500–600 MB（客户端 + 库 + 资源对象），整合包还要再加几百 MB，
/// 与其让用户在 90% 的时候看到「磁盘空间不足」的 IOException，不如一开始就说清楚。
/// </summary>
public static class DiskSpaceProbe
{
    /// <summary>目标路径所在盘符的可用字节数；问不出来时返回 null（不阻断流程）。</summary>
    public static long? FreeBytes(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return null;
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady) continue;
                if (!string.Equals(drive.RootDirectory.FullName.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    continue;
                return drive.AvailableFreeSpace;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // 网络盘 / 权限受限：宁可不提示，也不要因为探测失败挡住安装
        }
        return null;
    }

    /// <summary>空间不够时返回中文说明，够用或问不出来时返回 null。</summary>
    public static string? Describe(string? path, long requiredBytes)
    {
        if (requiredBytes <= 0) return null;
        if (FreeBytes(path) is not { } free) return null;
        if (free >= requiredBytes) return null;

        return $"磁盘空间不够：{Path.GetPathRoot(Path.GetFullPath(path!))} 需要至少 {Megabytes(requiredBytes)}，"
            + $"现在只剩 {Megabytes(free)}。\n清一点空间，或者换一个盘（可以在「选择游戏目录…」里改）。";
    }

    public static string Megabytes(long bytes) =>
        (bytes / 1024d / 1024d).ToString("N0", CultureInfo.InvariantCulture) + " MB";
}
