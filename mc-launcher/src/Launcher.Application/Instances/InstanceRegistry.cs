using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Launcher.Application.Install;

namespace Launcher.Application.Instances;

/// <summary>
/// 实例登记表里的一条：实例目录（<c>instances/&lt;Id&gt;</c>）对应哪个版本目录（<c>versions/&lt;VersionId&gt;</c>）。
/// 这两个 id 以前是同一个值，所以「同一个加载器版本导第二个整合包」会直接覆盖前一个实例；
/// 拆开之后实例可以叫 <c>fabric-loader-0.20.1-1.21.4-2</c>，版本目录仍然是 <c>fabric-loader-0.20.1-1.21.4</c>。
/// </summary>
public sealed record InstanceRegistration(
    string Id,
    string VersionId,
    string? Name,
    string? PackVersion,
    string? Source,
    string? Loader,
    string? MinecraftVersion,
    string? ImportedAt)
{
    /// <summary>列表显示的名字：有包名就是「通天之路 1.3.5」，没有就退回实例 id。</summary>
    [JsonIgnore]
    public string DisplayTitle =>
        string.IsNullOrWhiteSpace(Name)
            ? Id
            : string.IsNullOrWhiteSpace(PackVersion) ? Name! : $"{Name} {PackVersion}";

    /// <summary>副标题：加载器 + 游戏版本，例如「Fabric 0.20.1 · 1.21.4」。</summary>
    [JsonIgnore]
    public string? DisplaySubtitle
    {
        get
        {
            var parts = new List<string>(2);
            if (!string.IsNullOrWhiteSpace(Loader)) parts.Add(Loader);
            if (!string.IsNullOrWhiteSpace(MinecraftVersion)) parts.Add(MinecraftVersion);
            return parts.Count == 0 ? null : string.Join(" · ", parts);
        }
    }

    internal InstanceMetadata ToMetadata() =>
        new(Name ?? Id, PackVersion, Source, Loader, MinecraftVersion, ImportedAt, VersionId);
}

/// <summary>
/// 实例登记表：把「有哪些实例」从 <c>versions/</c> 目录里解放出来，落在 <c>instances/index.json</c>。
/// <para>
/// 三条约定：
/// ① <b>磁盘优先</b>——<c>instances/*/instance.json</c> 是事实来源，index.json 只是它的索引；
/// 索引丢了、写坏了、被手删了，<see cref="Load"/> 会按磁盘现状重建，重建结果写回索引（<see cref="Migrated"/>）。
/// ② <b>向后兼容</b>——老数据里实例目录名就是版本目录名、instance.json 里没有 <c>LoaderVersionId</c>，
/// 一律按「实例 id 就是版本 id」推导；只有 <c>versions/</c> 目录没有实例的版本，也照样登记成一条（界面以前就是这么显示的）。
/// ③ <b>命名冲突</b>——同一个版本目录要开第二个实例时依次用 <c>&lt;版本 id&gt;-2</c>、<c>-3</c>；同一份整合包重复导入则复用原实例。
/// </para>
/// </summary>
public sealed class InstanceRegistry
{
    /// <summary>索引文件名，落在 <c>instances/index.json</c>。</summary>
    public const string FileName = "index.json";

    private const int CurrentFormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly Regex SuffixPattern = new(@"^(?<base>.+)-\d+$", RegexOptions.Compiled);

    private readonly List<InstanceRegistration> _entries;

    private InstanceRegistry(string gameRoot, List<InstanceRegistration> entries, bool migrated)
    {
        GameRoot = gameRoot;
        _entries = entries;
        Migrated = migrated;
    }

    /// <summary>游戏根目录（<c>versions/</c>、<c>instances/</c> 的父目录）。</summary>
    public string GameRoot { get; }

    /// <summary>这次加载是不是重建过索引（索引缺失 / 内容与磁盘不一致都会置 true）。</summary>
    public bool Migrated { get; }

    /// <summary>全部实例，按 id 排序，顺序稳定。</summary>
    public IReadOnlyList<InstanceRegistration> Instances => _entries;

    /// <summary>索引文件路径。</summary>
    public static string PathFor(string gameRoot) => Path.Combine(gameRoot, "instances", FileName);

    /// <summary>
    /// 读登记表；索引缺失或与磁盘不一致时按磁盘重建（<b>不写盘</b>，要落盘用 <see cref="LoadOrMigrate"/>）。
    /// </summary>
    public static InstanceRegistry Load(string gameRoot) => LoadCore(gameRoot, persist: false);

    /// <summary>
    /// 读登记表；需要迁移（索引缺失 / 条目有变化）时顺手写回 <c>instances/index.json</c>。
    /// 启动时调用这一个就够了。
    /// </summary>
    public static InstanceRegistry LoadOrMigrate(string gameRoot) => LoadCore(gameRoot, persist: true);

    /// <summary>
    /// 实例 id → 版本目录 id。给「启动 / 装 Mod / 更新检查」用：
    /// 老实例直接返回自己（<c>versions/&lt;id&gt;/&lt;id&gt;.json</c> 存在时就是快路径），
    /// 带后缀的实例读 instance.json 里的 <c>LoaderVersionId</c>，再不行查索引。
    /// </summary>
    public static string ResolveVersionId(string gameRoot, string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return instanceId;

        // 快路径：老数据里两者同名，一个 File.Exists 就够了
        if (File.Exists(Path.Combine(gameRoot, "versions", instanceId, instanceId + ".json"))) return instanceId;

        if (InstanceMetadata.TryRead(gameRoot, instanceId)?.LoaderVersionId is { Length: > 0 } fromMetadata
            && Directory.Exists(Path.Combine(gameRoot, "versions", fromMetadata)))
        {
            return fromMetadata;
        }

        // 元数据里没写（老实例被排到 -2 又没重建过 metadata）：按 "<版本 id>-N" 的后缀还原
        var suffixed = SuffixPattern.Match(instanceId);
        if (suffixed.Success)
        {
            var baseId = suffixed.Groups["base"].Value;
            if (File.Exists(Path.Combine(gameRoot, "versions", baseId, baseId + ".json"))) return baseId;
        }

        foreach (var entry in ReadIndexPurified(gameRoot))
        {
            if (!string.Equals(entry.Id, instanceId, StringComparison.OrdinalIgnoreCase)) continue;
            if (Directory.Exists(Path.Combine(gameRoot, "versions", entry.VersionId))) return entry.VersionId;
        }

        return instanceId;
    }

    /// <summary>按 id 找一条（大小写不敏感）。</summary>
    public InstanceRegistration? Find(string instanceId) =>
        _entries.FirstOrDefault(e => string.Equals(e.Id, instanceId, StringComparison.OrdinalIgnoreCase));

    /// <summary>实例目录是否已经存在（有 instance.json 或至少有目录都算）。</summary>
    public bool Exists(string instanceId) =>
        Instances.Any(e => string.Equals(e.Id, instanceId, StringComparison.OrdinalIgnoreCase))
        || Directory.Exists(Path.Combine(GameRoot, "instances", instanceId));

    /// <summary>
    /// 给「要导入的整合包」分配实例目录名。
    /// 同一份包（名字 + 包版本 + 来源 + 版本目录都相同）重复导入会复用原实例；
    /// 否则版本目录空闲就用版本 id，已被占用就往后找 <c>-2</c>、<c>-3</c>。
    /// </summary>
    public string AllocateInstanceId(
        string versionId,
        string? packName = null,
        string? packVersion = null,
        string? source = null)
    {
        var same = _entries.FirstOrDefault(e =>
            string.Equals(e.VersionId, versionId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(e.Name, packName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(e.PackVersion, packVersion, StringComparison.OrdinalIgnoreCase)
            && string.Equals(e.Source, source, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(Path.Combine(GameRoot, "instances", e.Id)));

        if (same is not null) return same.Id;

        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 只有"真的有实例目录"的 id 才算占用。versions/ 下单独存在的版本（还没建实例、或刚装好加载器）
        // 只是列表里的虚拟条目，不能把新导入的实例挤到 -2。
        foreach (var entry in _entries)
        {
            if (Directory.Exists(Path.Combine(GameRoot, "instances", entry.Id))) taken.Add(entry.Id);
        }
        if (Directory.Exists(Path.Combine(GameRoot, "instances")))
        {
            foreach (var directory in Directory.EnumerateDirectories(Path.Combine(GameRoot, "instances")))
                taken.Add(Path.GetFileName(directory));
        }

        if (!taken.Contains(versionId)) return versionId;

        for (var n = 2; n < 1000; n++)
        {
            var candidate = $"{versionId}-{n}";
            if (!taken.Contains(candidate)) return candidate;
        }

        // 极端情况：999 个实例都占满了，退回时间戳，保证不覆盖别人
        return $"{versionId}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
    }

    /// <summary>
    /// 登记（或更新）一个实例并写回索引。元数据文件由调用方写（<see cref="InstanceMetadata.Write"/>），
    /// 这里只维护索引，避免两处写同一份文件。
    /// </summary>
    public InstanceRegistration Register(string instanceId, string versionId, InstanceMetadata metadata)
    {
        var entry = new InstanceRegistration(
            instanceId,
            versionId,
            metadata.Name,
            metadata.PackVersion,
            metadata.Source,
            metadata.Loader,
            metadata.MinecraftVersion,
            metadata.ImportedAt);

        _entries.RemoveAll(e => string.Equals(e.Id, instanceId, StringComparison.OrdinalIgnoreCase));
        _entries.Add(entry);
        Sort();
        Save();
        return entry;
    }

    /// <summary>删掉一条登记（实例目录由调用方删）。不存在返回 false。</summary>
    public bool Remove(string instanceId)
    {
        var removed = _entries.RemoveAll(e => string.Equals(e.Id, instanceId, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed)
        {
            Sort();
            Save();
        }

        return removed;
    }

    /// <summary>把当前登记表写回 <c>instances/index.json</c>。</summary>
    public void Save()
    {
        var path = PathFor(GameRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var payload = new IndexFile
        {
            FormatVersion = CurrentFormatVersion,
            UpdatedAt = DateTimeOffset.Now.ToString("o"),
            Instances = _entries.ToList(),
        };

        // 先写临时文件再替换：中途崩了也不会留下半截 JSON
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(payload, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    private static InstanceRegistry LoadCore(string gameRoot, bool persist)
    {
        var onDisk = ScanDisk(gameRoot);
        var fromIndex = ReadIndexPurified(gameRoot).ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);

        // 版本目录集合：用来给「没有 instance.json 的老实例」推导它挂在哪个版本上
        var versionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(Path.Combine(gameRoot, "versions")))
        {
            foreach (var directory in Directory.EnumerateDirectories(Path.Combine(gameRoot, "versions")))
            {
                var name = Path.GetFileName(directory);
                if (File.Exists(Path.Combine(directory, name + ".json"))) versionIds.Add(name);
            }
        }

        var entries = new List<InstanceRegistration>();

        // ① 实例目录（有 instance.json 的优先，没有的算"孤儿目录"也要登记，否则界面上会豁一块）
        foreach (var (id, metadata) in onDisk)
        {
            var versionId = metadata?.LoaderVersionId;
            if (string.IsNullOrWhiteSpace(versionId) && fromIndex.TryGetValue(id, out var indexed)) versionId = indexed.VersionId;
            if (string.IsNullOrWhiteSpace(versionId)) versionId = DeriveVersionId(id, versionIds);

            entries.Add(new InstanceRegistration(
                id,
                versionId!,
                metadata?.Name,
                metadata?.PackVersion,
                metadata?.Source,
                metadata?.Loader,
                metadata?.MinecraftVersion,
                metadata?.ImportedAt));
        }

        // ② 只有 versions/ 目录、没有实例目录的老版本：以前界面就是这么列的，保持可见
        var knownVersions = new HashSet<string>(
            entries.Select(e => e.VersionId), StringComparer.OrdinalIgnoreCase);
        foreach (var versionId in versionIds.OrderBy(v => v, StringComparer.OrdinalIgnoreCase))
        {
            if (knownVersions.Contains(versionId)) continue;
            entries.Add(new InstanceRegistration(versionId, versionId, null, null, null, null, null, null));
        }

        entries.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));

        var migrated = !SameAsIndex(entries, fromIndex);
        var registry = new InstanceRegistry(gameRoot, entries, migrated);
        if (migrated && persist) registry.Save();
        return registry;
    }

    /// <summary>
    /// 扫 <c>instances/</c> 下的实例目录。返回 id → 元数据（读不出来就是 null，不抛）。
    /// </summary>
    private static List<(string Id, InstanceMetadata? Metadata)> ScanDisk(string gameRoot)
    {
        var result = new List<(string, InstanceMetadata?)>();
        var instancesDirectory = Path.Combine(gameRoot, "instances");
        if (!Directory.Exists(instancesDirectory)) return result;

        foreach (var directory in Directory.EnumerateDirectories(instancesDirectory))
        {
            var id = Path.GetFileName(directory);
            if (string.IsNullOrWhiteSpace(id)) continue;
            result.Add((id, InstanceMetadata.TryRead(gameRoot, id)));
        }

        return result;
    }

    /// <summary>实例 id 推不出处（没有 LoaderVersionId）时：同名的版本目录 → 去掉 <c>-2</c> 后缀再试 → 原样。</summary>
    private static string DeriveVersionId(string instanceId, ISet<string> versionIds)
    {
        if (versionIds.Contains(instanceId)) return instanceId;

        var match = SuffixPattern.Match(instanceId);
        if (match.Success && versionIds.Contains(match.Groups["base"].Value)) return match.Groups["base"].Value;

        return instanceId;
    }

    /// <summary>索引里的条目：读不出来就当作没有索引（走迁移）。不校验磁盘，纯读文件。</summary>
    private static List<InstanceRegistration> ReadIndexPurified(string gameRoot)
    {
        var path = PathFor(gameRoot);
        try
        {
            if (!File.Exists(path)) return [];

            var file = JsonSerializer.Deserialize<IndexFile>(File.ReadAllText(path), JsonOptions);
            return file?.Instances?
                .Where(e => !string.IsNullOrWhiteSpace(e.Id) && !string.IsNullOrWhiteSpace(e.VersionId))
                .ToList() ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 索引坏了不是事故：磁盘上的 instance.json 才是事实来源
            return [];
        }
    }

    /// <summary>算出来的表和索引一致吗（id 集合 + 每条关键字段）。</summary>
    private static bool SameAsIndex(List<InstanceRegistration> computed, Dictionary<string, InstanceRegistration> index)
    {
        if (computed.Count != index.Count) return false;

        foreach (var entry in computed)
        {
            if (!index.TryGetValue(entry.Id, out var other)) return false;
            if (!string.Equals(entry.VersionId, other.VersionId, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.Equals(entry.Name, other.Name, StringComparison.Ordinal)) return false;
            if (!string.Equals(entry.PackVersion, other.PackVersion, StringComparison.Ordinal)) return false;
            if (!string.Equals(entry.Source, other.Source, StringComparison.Ordinal)) return false;
            if (!string.Equals(entry.Loader, other.Loader, StringComparison.Ordinal)) return false;
            if (!string.Equals(entry.MinecraftVersion, other.MinecraftVersion, StringComparison.Ordinal)) return false;
        }

        return true;
    }

    private void Sort() =>
        _entries.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));

    /// <summary>索引文件的形状。<c>formatVersion</c> 留给以后改结构，读到时不用管。</summary>
    private sealed class IndexFile
    {
        public int FormatVersion { get; set; } = CurrentFormatVersion;

        public string? UpdatedAt { get; set; }

        public List<InstanceRegistration> Instances { get; set; } = [];
    }
}
