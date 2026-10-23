using System.Text;

namespace Launcher.Domain.Launch;

/// <summary>启动计划的最终形态。生成之后才允许被平台层拿去起进程。</summary>
public sealed record LaunchPlan(
    string JavaPath,
    string MainClass,
    IReadOnlyList<string> JvmArguments,
    IReadOnlyList<string> GameArguments,
    string WorkingDirectory)
{
    /// <summary>
    /// 生成 Java 9+ 的 @argfile 内容。两个坑必须在这里一次性处理掉：
    /// 1) argfile 里反斜杠是转义符 → 全部换成正斜杠，否则 Windows 路径会被吃掉；
    /// 2) 含空格/引号的参数必须用双引号包住，内部引号转义。
    /// </summary>
    public string ToArgFileContent()
    {
        var builder = new StringBuilder();
        foreach (var arg in JvmArguments)
            builder.AppendLine(Quote(arg.Replace('\\', '/')));

        builder.AppendLine(Quote(MainClass));

        foreach (var arg in GameArguments)
            builder.AppendLine(Quote(arg.Replace('\\', '/')));

        return builder.ToString();
    }

    /// <summary>便于日志/诊断的完整命令行（令牌需调用方自行打码）。</summary>
    public string ToCommandLinePreview() =>
        string.Join(' ', new[] { JavaPath }.Concat(JvmArguments).Append(MainClass).Concat(GameArguments).Select(Quote));

    private static string Quote(string arg)
    {
        if (arg.Length == 0) return "\"\"";
        if (!arg.Any(c => c is ' ' or '\t' or '"')) return arg;
        return "\"" + arg.Replace("\"", "\\\"") + "\"";
    }
}
