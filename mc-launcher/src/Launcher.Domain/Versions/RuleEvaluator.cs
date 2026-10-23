using System.Text.RegularExpressions;

namespace Launcher.Domain.Versions;

/// <summary>规则求值。这里是"能不能启动"的第一道分水岭，建议单测覆盖。</summary>
public static class RuleEvaluator
{
    public static bool IsAllowed(IReadOnlyList<Rule>? rules, PlatformContext ctx)
    {
        if (rules is null || rules.Count == 0)
            return true;

        var allowed = false;
        foreach (var rule in rules)
        {
            if (!Matches(rule, ctx))
                continue;
            allowed = string.Equals(rule.Action, "allow", StringComparison.OrdinalIgnoreCase);
        }
        return allowed;
    }

    public static bool Matches(Rule rule, PlatformContext ctx)
    {
        if (rule.Os is { } os)
        {
            if (os.Name is not null && !string.Equals(os.Name, ctx.OsName, StringComparison.OrdinalIgnoreCase))
                return false;

            if (os.Arch is not null && !string.Equals(os.Arch, ctx.Arch, StringComparison.OrdinalIgnoreCase))
                return false;

            if (os.Version is not null && !SafeRegexMatch(ctx.OsVersion, os.Version))
                return false;
        }

        if (rule.Features is { } f)
        {
            // 我们不支持 demo / quick play，相关规则一律视为不匹配。
            if (f.IsDemoUser == true) return false;
            if (f.HasQuickPlaysSupport == true) return false;
            if (f.IsQuickPlaySingleplayer == true) return false;
            if (f.IsQuickPlayMultiplayer == true) return false;
            if (f.IsQuickPlayRealms == true) return false;

            if (f.HasCustomResolution == true && !ctx.HasCustomResolution) return false;
            if (f.HasCustomResolution == false && ctx.HasCustomResolution) return false;
        }

        return true;
    }

    private static bool SafeRegexMatch(string input, string pattern)
    {
        try { return Regex.IsMatch(input, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)); }
        catch (ArgumentException) { return false; }
    }
}
