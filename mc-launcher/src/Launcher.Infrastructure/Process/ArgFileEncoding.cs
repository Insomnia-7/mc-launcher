using System.Runtime.InteropServices;

namespace Launcher.Infrastructure.Processes;

/// <summary>
/// <c>@argfile</c> 的落盘编码。
///
/// JVM 在 Windows 上用<b>系统 ANSI 代码页</b>（<c>GetACP()</c>，简体中文机器是 936/GBK）解析
/// <c>@argfile</c>，不是 UTF-8。以前这里按 UTF-8 写，只要 classpath 里有一个非 ASCII 字符
/// （游戏目录 <c>D:\HBJ启动器</c>、用户名带中文的 <c>C:\Users\张三\AppData</c>），java 读到的
/// 路径就是乱码，客户端 jar 那条直接失效，于是报
/// <c>java.lang.ClassNotFoundException: net.minecraft.client.main.Main</c>
/// ——界面上的「启动失败：缺少类：net.minecraft.client.main.Main」就是它，和文件是否下载完整无关。
///
/// 这里只负责「能不能按 ANSI 写出来」：写不出来（代码页表示不了的字符）就返回 null，
/// 交给调用方走命令行直传那条更稳的路。
/// </summary>
public static class ArgFileEncoding
{
    private const uint NoFlags = 0;
    private const int CodePageAnsi = 0; // CP_ACP

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WideCharToMultiByte(
        int codePage,
        uint flags,
        string wideCharString,
        int wideCharCount,
        byte[]? multiByteString,
        int multiByteCount,
        IntPtr defaultChar,
        IntPtr usedDefaultChar);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MultiByteToWideChar(
        int codePage,
        uint flags,
        byte[] multiByteString,
        int multiByteCount,
        char[]? wideCharString,
        int wideCharCount);

    /// <summary>
    /// 按当前系统 ANSI 代码页编码。文本里存在该代码页表示不了的字符时返回 <c>null</c>
    /// （这时写出来的字节读回来会和原文不一致，等于又制造一次乱码）。
    /// </summary>
    public static byte[]? TryEncodeAnsi(string text)
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (text.Length == 0) return [];

        var count = WideCharToMultiByte(CodePageAnsi, NoFlags, text, text.Length, null, 0, IntPtr.Zero, IntPtr.Zero);
        if (count <= 0) return null;

        var bytes = new byte[count];
        if (WideCharToMultiByte(CodePageAnsi, NoFlags, text, text.Length, bytes, count, IntPtr.Zero, IntPtr.Zero) != count)
            return null;

        var back = MultiByteToWideChar(CodePageAnsi, NoFlags, bytes, count, null, 0);
        if (back <= 0) return null;

        var chars = new char[back];
        if (MultiByteToWideChar(CodePageAnsi, NoFlags, bytes, count, chars, back) != back) return null;

        return new string(chars) == text ? bytes : null;
    }
}
