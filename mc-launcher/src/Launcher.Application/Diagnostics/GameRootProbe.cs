using System;
using System.IO;
using System.Text;

namespace Launcher.Application.Diagnostics;

/// <summary>
/// 检查游戏目录能不能真正用来装东西。
/// 启动阶段的很多操作（建 instances/versions、写 version json、解 natives）都以它为前提，
/// 而“路径被一个文件占住”“盘是只读的”“父目录不存在又建不出来”这几种情况
/// 抛的都是 IOException / UnauthorizedAccessException——直接冒泡会让启动器连窗口都起不来。
/// </summary>
public static class GameRootProbe
{
    private const string ProbeFileName = ".mc-launcher-write-probe";

    /// <summary>判断异常是否属于“目录不可用”这一类预期失败（可以给用户看，不需要崩）。</summary>
    public static bool IsExpected(Exception exception) => exception switch
    {
        IOException => true,
        UnauthorizedAccessException => true,
        ArgumentException => true,
        NotSupportedException => true,
        _ => false,
    };

    /// <summary>
    /// 返回一句中文说明；目录可用时返回 null。
    /// 会在目录里写一个探针文件再删掉——只读盘就是靠这一步发现的。
    /// </summary>
    public static string? Describe(string? gameRoot)
    {
        if (string.IsNullOrWhiteSpace(gameRoot)) return "游戏目录没有设置";

        string full;
        try
        {
            full = Path.GetFullPath(gameRoot);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return $"游戏目录路径不合法：{ErrorText.Describe(ex)}";
        }

        if (File.Exists(full))
            return $"游戏目录指向了一个文件，不是文件夹：{full}。请把游戏目录改到别的路径。";

        try
        {
            Directory.CreateDirectory(full);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return $"游戏目录建不出来：{full}\n{ErrorText.Describe(ex)}\n"
                + "常见原因：上级路径里有一段是文件、盘符不存在、盘是只读的，或者没有写权限。";
        }

        var probe = Path.Combine(full, ProbeFileName + "-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            File.WriteAllText(probe, "probe", Encoding.UTF8);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return $"游戏目录不能写入：{full}\n{ErrorText.Describe(ex)}\n"
                + "换一个有写权限的目录（不要放在 Program Files 或只读盘里）。";
        }
        finally
        {
            try
            {
                if (File.Exists(probe)) File.Delete(probe);
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                // 探针文件删不掉不影响结论，下次启动会再探一次
            }
        }

        return null;
    }
}
