using System.ComponentModel;
using System.Text;

namespace Launcher.Application.Diagnostics;

/// <summary>
/// 被 SmartScreen / Defender / 第三方杀软拦下时的说明与处置步骤。
/// 既能在「启动器或游戏被拦住」时自动识别异常，也提供一份随时可看的静态帮助。
/// </summary>
public sealed record BlockedAppGuidance(string Title, string Summary, IReadOnlyList<string> Steps, string? Evidence = null)
{
    private static readonly string[] Keywords =
    [
        "smartscreen",
        "此应用已被阻止",
        "已被系统管理员阻止",
        "windows 已保护你的电脑",
        "已保护你的电脑",
        "blocked by",
        "被阻止",
        "病毒",
        "威胁",
        "defender",
        "杀毒",
        "安全软件",
        "access is denied",
        "拒绝访问",
        "操作已被取消",
        "0x800704ec",
    ];

    /// <summary>随时可看的那份帮助（没有具体异常时用）。</summary>
    public static BlockedAppGuidance Generic { get; } = new(
        "被安全软件拦住了？",
        "启动器是未签名的独立程序，SmartScreen、Defender 或第三方杀软可能把它当成可疑文件。下面四步能解决绝大多数情况。",
        [
            "解除单个文件的锁定：右键 MCLauncher.exe → 属性 → 常规页底部勾上「解除锁定」→ 确定。也可以在启动器目录里执行 Get-ChildItem -Recurse | Unblock-File。",
            "加排除项：Windows 安全中心 → 病毒和威胁防护 → 管理设置 → 排除项 → 添加文件夹，把启动器目录和游戏目录都加进去。",
            "SmartScreen 弹窗：点「更多信息」→「仍要运行」；如果是被组策略拦住（提示「已被系统管理员阻止」），需要管理员调整策略。",
            "核对来源：只从项目的 Release 页下载，用 Get-FileHash 比对 update.json 里的 sha256；哈希对不上说明文件被改过，不要放行。",
        ]);

    /// <summary>从异常里识别「被拦截」并给出针对性说明；不是这类异常返回 null。</summary>
    public static BlockedAppGuidance? TryFor(Exception? exception)
    {
        if (exception is null) return null;

        foreach (var ex in Flatten(exception))
        {
            if (ex is Win32Exception win32)
            {
                var code = win32.NativeErrorCode;
                if (code == 1223) return Cancelled();
                if (code == 225) return Infected(win32.Message);
                if (code is 1260 or 1275) return Policy(win32.Message);
                if (code == 5) return Denied(win32.Message);
                if (code == 740) return NeedsElevation(win32.Message);
            }

            if (ex is UnauthorizedAccessException && LooksBlocked(ex.Message)) return Denied(ex.Message);
            if (LooksBlocked(ex.Message)) return Generic with { Evidence = Trim(ex.Message) };
        }

        return null;
    }

    public static bool LooksBlocked(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var lower = text.ToLowerInvariant();
        foreach (var keyword in Keywords)
        {
            if (lower.Contains(keyword, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    public string ToDisplayText()
    {
        var builder = new StringBuilder();
        builder.AppendLine(Title);
        builder.AppendLine(Summary);
        builder.AppendLine();
        for (var index = 0; index < Steps.Count; index++)
        {
            builder.AppendLine($"{index + 1}. {Steps[index]}");
        }

        if (!string.IsNullOrWhiteSpace(Evidence))
        {
            builder.AppendLine();
            builder.AppendLine("系统返回的原文：" + Evidence);
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>只给步骤（标题与摘要由界面单独显示，避免重复一遍）。</summary>
    public string ToStepsText()
    {
        var builder = new StringBuilder();
        for (var index = 0; index < Steps.Count; index++)
        {
            builder.AppendLine($"{index + 1}. {Steps[index]}");
        }

        if (!string.IsNullOrWhiteSpace(Evidence))
        {
            builder.AppendLine();
            builder.AppendLine("系统返回的原文：" + Evidence);
        }

        return builder.ToString().TrimEnd();
    }
    private static IEnumerable<Exception> Flatten(Exception exception)
    {
        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.Flatten().InnerExceptions)
            {
                foreach (var item in Flatten(inner)) yield return item;
            }

            yield break;
        }

        yield return exception;
        if (exception.InnerException is { } innerException)
        {
            foreach (var item in Flatten(innerException)) yield return item;
        }
    }

    private static string Trim(string message) =>
        message.Length <= 300 ? message : message[..300] + "…";

    private static BlockedAppGuidance Cancelled() => Generic with
    {
        Title = "启动被取消了",
        Summary = "Windows 在启动这个程序时弹了权限确认，但确认框被取消（或超时自动取消）。再点一次「启动游戏」，这次在弹窗里选「是」。",
        Evidence = null,
    };

    private static BlockedAppGuidance Infected(string message) => Generic with
    {
        Title = "文件被杀软判定为病毒并隔离",
        Summary = "系统报告这个文件已感染或已被安全软件拿走，程序本身没能启动。确认文件来自官方 Release 后，把启动器目录加入排除项再重新下载。",
        Evidence = Trim(message),
    };

    private static BlockedAppGuidance Policy(string message) => Generic with
    {
        Title = "被系统策略拦住",
        Summary = "这台机器（或域策略）禁止运行未签名的程序，个人设置里改不了，需要管理员调整 AppLocker / 软件限制策略。",
        Evidence = Trim(message),
    };

    private static BlockedAppGuidance Denied(string message) => Generic with
    {
        Title = "没有权限执行",
        Summary = "文件被占用、被杀软锁定，或当前账户对该目录没有执行权限。先关掉占用它的程序，再按下面四步排查。",
        Evidence = Trim(message),
    };

    private static BlockedAppGuidance NeedsElevation(string message) => Generic with
    {
        Title = "需要管理员权限",
        Summary = "这个程序要求以管理员身份运行。右键 → 以管理员身份运行，或把启动器放到用户目录（如 D:\\MCLauncher）而不是 Program Files。",
        Evidence = Trim(message),
    };
}
