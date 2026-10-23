using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Launcher.Domain.Launch;

namespace Launcher.Application.Install;

/// <summary>
/// 单个实例的「怎么跑」配置，落在 <c>instances/&lt;id&gt;/settings.json</c>。
/// 和 <see cref="InstanceMetadata"/> 分工：那个记录「这个实例从哪来」（包名/加载器/导入时间），
/// 这个记录用户可改的运行参数（内存、Java、额外参数）。
/// </summary>
public sealed record InstanceSettings(
    // 4096 只是「显式构造时的占位值」。真正决定默认值的是 Load()/CreateDefault()：
    // settings.json 不存在或没有 MemoryMb 字段 → 按本机物理内存自适应（见 DefaultMemoryMb）。
    int MemoryMb = 4096,
    string? JavaPath = null,
    string? ExtraJvmArgs = null,
    string? ExtraGameArgs = null,
    int WindowWidth = 1280,
    int WindowHeight = 720,
    bool FullScreen = false,
    // 落盘成字符串（"default" / "fast" / "compatible"）：settings.json 是给人看的，
    // 存数字的话改档位得先查 0/1/2 的对应关系。老文件没有这个字段时按 Default 读。
    // 转换器挂在下面的 SerializerOptions 上（CamelCase），属性本身不加特性——
    // 特性写法没法传命名策略，那样落盘会是首字母大写的 "Fast"，和命令行的 fast 不一致。
    JvmPreset JvmPreset = JvmPreset.Default)
{
    public const string FileName = "settings.json";

    public const int MinMemoryMb = 512;
    public const int MaxMemoryMb = 65536;

    // 自适应默认值的形状：物理内存的 1/4，夹在 [2G, 16G]，再向下取整到 512MB 的倍数。
    // 夹住下界是因为 1G 物理内存的机器给 256MB 连原版都跑不起来；夹住上界是因为
    // 32G/64G 机器给 8G/16G 已经远超 MC 的实际需要，再大只会拖长 GC 停顿。
    public const int MinAdaptiveMemoryMb = 2048;
    public const int MaxAdaptiveMemoryMb = 16384;
    public const int MemoryStepMb = 512;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        // 枚举写字符串、写小写；读的时候大小写和数字都认（allowIntegerValues 默认 true），
        // 所以手改成 "Fast" / 1 / 99 都进得来，99 再由 Normalized() 收敛。
        Converters = { new JsonStringEnumConverter<JvmPreset>(JsonNamingPolicy.CamelCase) },
    };

    // 空格分隔，支持用双引号包住带空格的参数
    private static readonly Regex ArgumentPattern = new(
        "[^\\s\"]+|\"(?:[^\"\\\\]|\\\\.)*\"",
        RegexOptions.Compiled);

    public static string PathFor(string gameRoot, string instanceId) =>
        Path.Combine(gameRoot, "instances", instanceId, FileName);

    /// <summary>
    /// 按物理内存算默认堆大小：<c>min(16384, max(2048, 物理MB/4))</c>，再向下取整到 512 的倍数。
    /// 纯函数（只吃一个 long），测试直接喂 2G/8G/16G/64G 的字节数即可，不需要读本机内存。
    /// </summary>
    public static int DefaultMemoryMb(long totalAvailableMemoryBytes)
    {
        if (totalAvailableMemoryBytes <= 0) return MinAdaptiveMemoryMb;

        var physicalMb = totalAvailableMemoryBytes / (1024L * 1024L);
        var clamped = Math.Clamp(physicalMb / 4, (long)MinAdaptiveMemoryMb, (long)MaxAdaptiveMemoryMb);

        // 先夹后取整：夹完的都是 512 的倍数，取整只是防御未来改常量时忘了这层约定
        return (int)(clamped / MemoryStepMb * MemoryStepMb);
    }

    /// <summary>
    /// 当前机器的自适应默认值。<c>GC.GetGCMemoryInfo().TotalAvailableMemoryBytes</c> 是运行时
    /// 认为「自己最多能用多少内存」——在容器/cgroup 里它就是限额，比 <c>Win32_ComputerSystem</c>
    /// 的物理内存更接近真话，而且不用引 System.Management 之类的额外包。
    /// </summary>
    public static int AdaptiveMemoryMb() =>
        DefaultMemoryMb(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);

    /// <summary>
    /// 「这个实例没有明确配置」时的设置对象。用自适应内存，其余字段就是记录默认值。
    /// </summary>
    public static InstanceSettings CreateDefault() => new() { MemoryMb = AdaptiveMemoryMb() };

    public static InstanceSettings Load(string gameRoot, string instanceId)
    {
        var path = PathFor(gameRoot, instanceId);
        try
        {
            if (!File.Exists(path)) return CreateDefault();

            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<InstanceSettings>(json, SerializerOptions) ?? CreateDefault();

            // 「字段缺失」和「用户写了 4096」必须区分开：老 settings.json 里根本没有 MemoryMb，
            // 那种情况才该按本机内存推默认值；显式写过的一律尊重用户。
            return DeclaresMemoryMb(json) ? settings : settings with { MemoryMb = AdaptiveMemoryMb() };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 配置坏了就退回默认值，别让启动流程挂在这
            return CreateDefault();
        }
    }

    /// <summary>JSON 里是否真的出现了 <c>MemoryMb</c> 字段（大小写不敏感，值为 null 不算）。</summary>
    private static bool DeclaresMemoryMb(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, nameof(MemoryMb), StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.Number)
                    return true;
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public void Save(string gameRoot, string instanceId)
    {
        var path = PathFor(gameRoot, instanceId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, this, SerializerOptions);
    }

    /// <summary>把界面里填的一行参数拆成数组，支持 <c>"D:\a b\x.jar"</c> 这种带空格的写法。</summary>
    public static string[] SplitArguments(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : ArgumentPattern.Matches(text).Select(m => m.Value.Trim('"')).ToArray();

    public InstanceSettings Normalized() => this with
    {
        MemoryMb = Math.Clamp(MemoryMb, MinMemoryMb, MaxMemoryMb),
        WindowWidth = WindowWidth <= 0 ? 1280 : WindowWidth,
        WindowHeight = WindowHeight <= 0 ? 720 : WindowHeight,
        JavaPath = string.IsNullOrWhiteSpace(JavaPath) ? null : JavaPath.Trim(),
        ExtraJvmArgs = string.IsNullOrWhiteSpace(ExtraJvmArgs) ? null : ExtraJvmArgs.Trim(),
        ExtraGameArgs = string.IsNullOrWhiteSpace(ExtraGameArgs) ? null : ExtraGameArgs.Trim(),
        // 手工改坏的值（"turbo" / 99）不该让启动流程抛异常，静默回到 Default
        JvmPreset = JvmPresets.Normalize(JvmPreset),
    };
}
