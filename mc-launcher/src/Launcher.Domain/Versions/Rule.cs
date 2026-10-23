using System.Text.Json.Serialization;

namespace Launcher.Domain.Versions;

/// <summary>
/// 一条规则。语义（必须严格按这个来，写错就会在 32 位系统或跨平台上拿错库）：
/// 没有 rules = 允许；有 rules = 逐条匹配，最后一条命中的 action 决定结果。
/// </summary>
public sealed class Rule
{
    [JsonPropertyName("action")]
    public string Action { get; init; } = "";

    [JsonPropertyName("os")]
    public OsRule? Os { get; init; }

    [JsonPropertyName("features")]
    public FeaturesRule? Features { get; init; }
}

public sealed class OsRule
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("arch")]
    public string? Arch { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }
}

public sealed class FeaturesRule
{
    [JsonPropertyName("is_demo_user")]
    public bool? IsDemoUser { get; init; }

    [JsonPropertyName("has_custom_resolution")]
    public bool? HasCustomResolution { get; init; }

    [JsonPropertyName("has_quick_plays_support")]
    public bool? HasQuickPlaysSupport { get; init; }

    [JsonPropertyName("is_quick_play_singleplayer")]
    public bool? IsQuickPlaySingleplayer { get; init; }

    [JsonPropertyName("is_quick_play_multiplayer")]
    public bool? IsQuickPlayMultiplayer { get; init; }

    [JsonPropertyName("is_quick_play_realms")]
    public bool? IsQuickPlayRealms { get; init; }
}

/// <summary>运行平台上下文。os.name 用 Mojang 的词汇：windows / osx / linux。</summary>
public sealed record PlatformContext(string OsName, string Arch, string OsVersion)
{
    public static PlatformContext Current { get; } = Detect();

    public static PlatformContext Detect()
    {
        var os = OperatingSystem.IsWindows() ? "windows"
               : OperatingSystem.IsMacOS() ? "osx"
               : "linux";

        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X86 => "x86",
            System.Runtime.InteropServices.Architecture.X64 => "x86_64",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            System.Runtime.InteropServices.Architecture.Arm => "arm32",
            _ => "unknown",
        };

        return new PlatformContext(os, arch, Environment.OSVersion.Version.ToString());
    }

    /// <summary>用户是否会用到自定义分辨率（决定 has_custom_resolution 规则）。</summary>
    public bool HasCustomResolution { get; init; }
}
