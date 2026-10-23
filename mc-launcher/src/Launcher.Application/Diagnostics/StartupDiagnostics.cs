using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Launcher.Application.Diagnostics;

/// <summary>一条启动失败的诊断结论。</summary>
public sealed record StartupDiagnosis(string Summary, string Advice, string? Evidence = null)
{
    public string ToDisplayText() => string.IsNullOrWhiteSpace(Advice) ? Summary : $"{Summary}。{Advice}";
}

/// <summary>
/// 把 Minecraft 的崩溃日志翻译成人话。
/// 启动器最常见的失败面就那么几种：模块冲突、缺依赖、Java 版本、内存、驱动，
/// 日志里都有稳定可认的签名，认出来比让用户翻 2000 行 stack trace 有用得多。
/// </summary>
public static partial class StartupDiagnostics
{
    /// <summary>只读日志尾部：崩溃结论永远在最后，整份读完没必要。</summary>
    private const int TailBytes = 128 * 1024;

    private sealed record Rule(Func<string, StartupDiagnosis?> Match, int Priority);

    private static readonly Rule[] Rules = BuildRules();

    /// <summary>日志不存在或认不出来时返回 null，调用方退回「看日志」的兜底提示。</summary>
    public static StartupDiagnosis? Analyze(string logPath)
    {
        var text = ReadTail(logPath);
        if (text is null) return null;

        foreach (var rule in Rules)
        {
            if (rule.Match(text) is { } diagnosis) return diagnosis;
        }

        return null;
    }

    private static string? ReadTail(string logPath)
    {
        try
        {
            if (!File.Exists(logPath)) return null;

            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var length = (int)Math.Min(stream.Length, TailBytes);
            stream.Seek(-length, SeekOrigin.End);

            var buffer = new byte[length];
            var read = stream.Read(buffer, 0, length);
            return Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Rule[] BuildRules()
    {
        var rules = new List<Rule>();

        // 模块系统：Forge 1.17+ 上两个 jar 导出同一个包。CustomSkinLoader 这类改原版类的 mod 常见。
        rules.Add(new Rule(text =>
        {
            var m = ModuleResolutionPattern().Match(text);
            if (!m.Success) return null;
            var a = m.Groups["a"].Value;
            var b = m.Groups["b"].Value;

            // minecraft 是游戏本体自己的模块，撞上它说明对方是个改写原版类的 mod
            var advice = a == "minecraft" ? ModAgainstVanilla(b)
                : b == "minecraft" ? ModAgainstVanilla(a)
                : $"这两个模组改了同一批原版类。在 mods 里先禁用其中一个再启动";

            return new StartupDiagnosis($"模组模块冲突：{a} 与 {b} 导出同一个包", advice, m.Value);
        }, 100));

        // Mixin 注入失败：目标方法被别的模组改过，或版本不对。
        rules.Add(new Rule(text =>
        {
            var m = MixinFailedPattern().Match(text);
            if (!m.Success) return null;
            return new StartupDiagnosis(
                $"Mixin 注入失败：{m.Groups["target"].Value}",
                "该模组注入的目标被别的模组改掉了，或者模组版本与游戏版本不符。禁用最近加的模组试试",
                m.Value);
        }, 95));

        rules.Add(new Rule(text =>
        {
            if (!text.Contains("java.lang.OutOfMemoryError", StringComparison.Ordinal)) return null;
            return new StartupDiagnosis(
                "内存不够（Java heap space）",
                "展开「版本设置」把内存调大（这个整合包建议 6144 MB 以上），或者减少同时装的模组",
                "java.lang.OutOfMemoryError");
        }, 90));

        rules.Add(new Rule(text =>
        {
            var m = UnsupportedClassVersionPattern().Match(text);
            if (!m.Success) return null;
            return new StartupDiagnosis(
                $"Java 版本不对：这个 class 需要 Java {m.Groups["required"].Value}，当前运行时不支持",
                "在「版本设置」里换一个更高版本的 Java；启动器扫描到的 Java 会列在下拉框里",
                m.Value);
        }, 85));

        rules.Add(new Rule(text =>
        {
            var m = MissingClassPattern().Match(text);
            if (!m.Success) return null;
            return new StartupDiagnosis(
                $"缺少类：{Shorten(m.Groups["type"].Value)}",
                "多半是依赖模组没装全，或某个 mod 文件没下完。重新导入整合包 / 补装依赖",
                m.Value);
        }, 80));

        rules.Add(new Rule(text =>
        {
            if (!text.Contains("Could not find or load main class", StringComparison.Ordinal)
                && !text.Contains("Error: Could not find or load main class", StringComparison.Ordinal)) return null;
            return new StartupDiagnosis(
                "主类没找到（classpath 不完整）",
                "版本文件损坏或库缺失。重新下载这个版本，或者重新导入整合包",
                "Could not find or load main class");
        }, 75));

        rules.Add(new Rule(text =>
        {
            var m = NoSuchFilePattern().Match(text);
            if (!m.Success) return null;
            return new StartupDiagnosis(
                $"文件缺失：{Shorten(m.Groups["path"].Value)}",
                "版本文件不全。用「下载中心」重装这个版本，或者重新导入整合包",
                m.Value);
        }, 70));

        rules.Add(new Rule(text =>
        {
            if (!text.Contains("EXCEPTION_ACCESS_VIOLATION", StringComparison.Ordinal)) return null;
            return new StartupDiagnosis(
                "JVM 崩溃（原生层访问越界）",
                "通常是显卡驱动、光影或带 native 库的模组。先关光影，再更新显卡驱动",
                "EXCEPTION_ACCESS_VIOLATION");
        }, 60));

        rules.Add(new Rule(text =>
        {
            if (!text.Contains("GLFW error", StringComparison.Ordinal)
                && !text.Contains("Failed to create window", StringComparison.Ordinal)) return null;
            return new StartupDiagnosis(
                "创建游戏窗口失败",
                "显卡驱动或 OpenGL 版本问题。更新驱动，或把游戏设置里的渲染方式改回默认",
                "GLFW error");
        }, 55));

        rules.Add(new Rule(text =>
        {
            if (!text.Contains("Address already in use", StringComparison.Ordinal)) return null;
            return new StartupDiagnosis(
                "端口被占用",
                "已经有一个游戏进程在跑，先关掉它再启动",
                "Address already in use");
        }, 50));

        rules.Add(new Rule(text =>
        {
            if (!text.Contains("A fatal error has been detected by the Java Runtime Environment", StringComparison.Ordinal))
                return null;
            return new StartupDiagnosis(
                "Java 运行时崩溃",
                "看同目录下的 hs_err 文件里的崩溃帧；常见原因是驱动、内存或模组 native 库",
                "A fatal error has been detected");
        }, 45));

        return rules.ToArray();
    }

    /// <summary>包名太长塞不下状态栏，只留最后两段。</summary>
    private static string Shorten(string value)
    {
        if (value.Length <= 60) return value;
        var trimmed = value.TrimStart('[', 'L');
        var parts = trimmed.Split('.');
        return parts.Length > 2 ? string.Join('.', parts[^2..]) : trimmed;
    }

    private static string ModAgainstVanilla(string modName) =>
        $"{modName} 改写了原版类，和游戏本体自己的模块撞了。到 mods 目录里禁用 {modName} 再启动"
        + "（这类模组通常是皮肤 / 界面增强，换个与游戏版本对得上的版本也能解决）";

    [GeneratedRegex("Modules (?<a>[\\w.]+) and (?<b>[\\w.]+) export package", RegexOptions.Compiled)]
    private static partial Regex ModuleResolutionPattern();

    [GeneratedRegex("Mixin transformation of (?<target>\\S+) failed", RegexOptions.Compiled)]
    private static partial Regex MixinFailedPattern();

    [GeneratedRegex("class file version (?<required>\\d+)\\.\\d+", RegexOptions.Compiled)]
    private static partial Regex UnsupportedClassVersionPattern();

    [GeneratedRegex("(?:ClassNotFoundException|NoClassDefFoundError): (?<type>[\\w.$/]+)", RegexOptions.Compiled)]
    private static partial Regex MissingClassPattern();

    [GeneratedRegex("(?:java\\.nio\\.file\\.NoSuchFileException|FileNotFoundException): (?<path>[^\\r\\n]+)", RegexOptions.Compiled)]
    private static partial Regex NoSuchFilePattern();
}
