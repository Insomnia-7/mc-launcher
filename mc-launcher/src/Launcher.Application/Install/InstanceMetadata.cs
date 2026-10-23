using System.Text.Json;
using System.Text.Json.Serialization;

namespace Launcher.Application.Install;

/// <summary>
/// 实例元数据，落在 <c>instances/&lt;id&gt;/instance.json</c>。
/// 版本目录名是 maven 风格的 id（<c>1.20.1-forge-47.4.18</c>），不适合直接给用户看；
/// 这个文件记住"这是哪个整合包"，界面上优先显示它，读不到就退回版本 id。
/// </summary>
/// <param name="LoaderVersionId">
/// 实例挂在哪个 <c>versions/</c> 目录上。同一套加载器可以被多个整合包各开一个实例
/// （实例目录叫 <c>fabric-loader-0.20.1-1.21.4-2</c>），版本目录仍然共用这一个。
/// 老数据没有这个字段，读出来是 null，由 <see cref="Launcher.Application.Instances.InstanceRegistry"/> 按目录名推导。
/// </param>
public sealed record InstanceMetadata(
    string Name,
    string? PackVersion,
    string? Source,
    string? Loader,
    string? MinecraftVersion,
    string? ImportedAt,
    string? LoaderVersionId = null)
{
    public const string FileName = "instance.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string PathFor(string gameRoot, string instanceId) =>
        Path.Combine(gameRoot, "instances", instanceId, FileName);

    /// <summary>文件不存在、内容损坏、权限不足都返回 null —— 界面退回版本 id，不要因为元数据把界面搞挂。</summary>
    public static InstanceMetadata? TryRead(string gameRoot, string instanceId)
    {
        try
        {
            var path = PathFor(gameRoot, instanceId);
            if (!File.Exists(path)) return null;

            var metadata = JsonSerializer.Deserialize<InstanceMetadata>(
                File.ReadAllText(path), Options);
            return string.IsNullOrWhiteSpace(metadata?.Name) ? null : metadata;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Write(string gameRoot, string instanceId)
    {
        var path = PathFor(gameRoot, instanceId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
    }

    /// <summary>列表与标题用的名字：有包名就「通天之路 1.3.5」，否则退回版本 id。</summary>
    public string ToDisplayTitle(string fallback)
    {
        if (string.IsNullOrWhiteSpace(Name)) return fallback;
        return string.IsNullOrWhiteSpace(PackVersion) ? Name : $"{Name} {PackVersion}";
    }

    /// <summary>副标题：加载器 + 游戏本体版本，例如「Forge 47.4.18 · 1.20.1」。</summary>
    public string? ToDisplaySubtitle()
    {
        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(Loader)) parts.Add(Loader);
        if (!string.IsNullOrWhiteSpace(MinecraftVersion)) parts.Add(MinecraftVersion);
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }
}
