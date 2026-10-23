using System.Text;
using Launcher.Domain.Versions;

namespace Launcher.Domain.Launch;

/// <summary>占位符替换。漏一个就启动失败，所以未知占位符保留原样而不是替换成空串（便于定位）。</summary>
public static class ArgumentExpander
{
    public static string Expand(string raw, IReadOnlyDictionary<string, string> vars)
    {
        if (raw.IndexOf("${", StringComparison.Ordinal) < 0) return raw;

        var builder = new StringBuilder(raw.Length + 16);
        var i = 0;

        while (i < raw.Length)
        {
            var start = raw.IndexOf("${", i, StringComparison.Ordinal);
            if (start < 0)
            {
                builder.Append(raw, i, raw.Length - i);
                break;
            }

            builder.Append(raw, i, start - i);

            var end = raw.IndexOf('}', start + 2);
            if (end < 0)
            {
                builder.Append(raw, start, raw.Length - start);
                break;
            }

            var name = raw[(start + 2)..end];
            if (vars.TryGetValue(name, out var value)) builder.Append(value);
            else builder.Append(raw, start, end - start + 1);   // 未知占位符原样保留

            i = end + 1;
        }

        return builder.ToString();
    }

    /// <summary>按 rules 过滤后逐条展开；空串与单空格（官方 JSON 里的占位）丢弃。</summary>
    public static List<string> ExpandAll(
        IEnumerable<ArgumentValue> values,
        PlatformContext ctx,
        IReadOnlyDictionary<string, string> vars)
    {
        var result = new List<string>();

        foreach (var value in values)
        {
            if (!RuleEvaluator.IsAllowed(value.Rules, ctx)) continue;
            if (value.Value is null) continue;

            var expanded = Expand(value.Value, vars);
            if (expanded.Length == 0 || expanded == " ") continue;

            result.Add(expanded);
        }

        return result;
    }

    /// <summary>把用户填的自由文本（JVM/游戏参数）拆成参数列表，支持双引号包裹。</summary>
    public static IReadOnlyList<string> SplitArguments(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;

        foreach (var c in text)
        {
            switch (c)
            {
                case '"':
                    inQuotes = !inQuotes;
                    hasToken = true;
                    break;
                case ' ' or '\t' when !inQuotes:
                    if (hasToken)
                    {
                        result.Add(current.ToString());
                        current.Clear();
                        hasToken = false;
                    }
                    break;
                default:
                    current.Append(c);
                    hasToken = true;
                    break;
            }
        }

        if (hasToken) result.Add(current.ToString());
        return result;
    }
}
