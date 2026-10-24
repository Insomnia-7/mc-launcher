using System;
using System.IO;

namespace Launcher.Application.Diagnostics;

/// <summary>
/// 区分「磁盘满了」和「网络坏了」。
/// 磁盘满时 Windows 抛的是 <see cref="IOException"/>，HResult 是 0x80070070（磁盘空间不足）
/// 或 0x80070027（配额不足）——和网络错误是同一个类型，只按类型重试只会白撞三次。
/// </summary>
public static class DiskErrors
{
    private const int DiskFull = 0x70;      // ERROR_DISK_FULL
    private const int DiskQuota = 0x27;     // 配额 / 句柄所在卷已满

    public static bool IsDiskFull(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is not IOException) continue;
            var code = current.HResult & 0xFFFF;
            if (code is DiskFull or DiskQuota) return true;

            var message = current.Message;
            if (message.Contains("磁盘空间不足", StringComparison.Ordinal)
                || message.Contains("not enough space", StringComparison.OrdinalIgnoreCase)
                || message.Contains("disk full", StringComparison.OrdinalIgnoreCase)
                || message.Contains("No space left on device", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>磁盘满时给用户看的一句话（带上是哪个文件写不进去）。</summary>
    public static string DescribeDiskFull(string? path = null)
    {
        var where = string.IsNullOrWhiteSpace(path) ? string.Empty : $"（{path}）";
        return $"磁盘空间不足，已经停止下载{where}。\n清理一些空间或换一个盘后重试；已下载的部分会续传，不用从头再来。";
    }
}
