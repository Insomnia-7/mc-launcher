using System.Text.Json;
using Launcher.Application.Abstractions;
using Launcher.Application.Install;
using Launcher.Domain.Modpacks;

namespace Launcher.Infrastructure.Modpacks;

/// <summary>
/// MultiMC / Prism Launcher 实例。和 Modrinth / CurseForge 包最大的区别：实例是<b>文件夹</b>，不是 zip。
/// <code>
///   &lt;instance&gt;/mmc-pack.json   组件清单：net.minecraft + 加载器
///   &lt;instance&gt;/instance.cfg    INI 文本，取 name（实例显示名）
///   &lt;instance&gt;/minecraft/      真正的游戏目录（老实例可能叫 .minecraft/），内容就是 overrides
/// </code>
/// 映射到 <see cref="ModrinthPack"/>：<see cref="ModrinthPack.Files"/> 恒为空——所有文件都已经躺在实例目录里，
/// 没有任何要从网上下载的东西；<see cref="ExtractOverridesAsync"/> 负责把游戏目录整棵复制到目标位置。
/// mmc-pack.json / instance.cfg 在构造时一次读进内存，之后 <see cref="Pack"/> 是不可变的。
/// </summary>
public sealed class MultiMcPackSource : IModpackSource
{
    private const string PackJsonName = "mmc-pack.json";
    private const string InstanceConfigName = "instance.cfg";

    // MultiMC 组件 uid（Prism 沿用同一套）
    private const string MinecraftComponent = "net.minecraft";
    private const string ForgeComponent = "net.minecraftforge";
    private const string NeoForgeComponent = "net.neoforged";
    private const string NeoForgeNeoformComponent = "net.neoforged.neoform";
    private const string FabricComponent = "net.fabricmc.fabric-loader";
    private const string QuiltComponent = "org.quiltmc.quilt-loader";

    // 写进 ModrinthPack.Dependencies 的键，和 modrinth.index.json 的 dependencies 保持一致
    private const string MinecraftDependency = "minecraft";
    private const string ForgeDependency = "forge";
    private const string NeoForgeDependency = "neoforge";
    private const string FabricDependency = "fabric-loader";
    private const string QuiltDependency = "quilt-loader";

    /// <summary>MultiMC 没有「整合包版本」这个概念，给个固定值填进实例元数据。</summary>
    private const string FixedPackVersion = "1.0.0";

    private const string CopyStage = "复制 overrides";
    private const int ProgressEvery = 50;

    /// <summary>游戏目录候选名，顺序即优先级。</summary>
    private static readonly string[] GameDirectoryNames = ["minecraft", ".minecraft"];

    private readonly string? _gameDirectory;

    private MultiMcPackSource(string instanceDirectory, string? gameDirectory, ModrinthPack pack)
    {
        SourcePath = instanceDirectory;
        _gameDirectory = gameDirectory;
        Pack = pack;
    }

    /// <summary>实例文件夹的绝对路径（MultiMC 实例没有「源文件」，这里给目录）。</summary>
    public string SourcePath { get; }

    public ModrinthPack Pack { get; }

    /// <summary>判断这个路径是不是 MultiMC / Prism 实例文件夹（含 mmc-pack.json）。路径无效一律 false。</summary>
    public static bool IsMultiMcInstance(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            if (!Directory.Exists(path)) return false;
            return FindFile(path, PackJsonName) is not null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 路径非法、没权限、盘掉线：都不算实例
            return false;
        }
    }

    /// <summary>
    /// 打开实例。不是 MultiMC 实例时抛 <see cref="InvalidOperationException"/>，原因写清楚
    /// （路径为空 / 是 .zip 文件 / 目录不存在 / 缺 mmc-pack.json）；
    /// 是实例但 mmc-pack.json 本身坏了，抛 <see cref="InvalidDataException"/>。
    /// </summary>
    public static MultiMcPackSource Open(string path)
    {
        var instanceDirectory = ResolveInstanceDirectory(path);

        var packJsonPath = FindFile(instanceDirectory, PackJsonName)!;
        var components = ParseComponents(packJsonPath);

        string? configPath;
        try
        {
            configPath = FindFile(instanceDirectory, InstanceConfigName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            configPath = null; // 读不了 instance.cfg 只影响名字，回退文件夹名
        }

        var name = ParseInstanceConfig(configPath).GetValueOrDefault("name")?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            name = new DirectoryInfo(instanceDirectory).Name;

        var pack = new ModrinthPack
        {
            FormatVersion = 1,
            Game = "minecraft",
            VersionId = FixedPackVersion,
            Name = name,
            SourceLabel = "MultiMC",
            Files = [],
            Dependencies = BuildDependencies(components),
        };

        var gameDirectory = FindGameDirectory(instanceDirectory);
        return new MultiMcPackSource(instanceDirectory, gameDirectory, pack);
    }

    /// <summary>
    /// 文件全在实例目录里，<see cref="ExtractOverridesAsync"/> 会整棵复制过去，没有要下载的。
    /// 行为与 <see cref="IModpackSource.ResolveFilesAsync"/> 的接口默认实现一致（默认实现调不到具体类型上，这里显式给一份）。
    /// </summary>
    public Task<IReadOnlyList<ModpackFile>> ResolveFilesAsync(
        IProgress<ModpackImportProgress>? progress = null,
        CancellationToken ct = default) => Task.FromResult(Pack.Files);

    /// <summary>
    /// 把游戏目录（<c>minecraft/</c> 优先，没有则 <c>.minecraft/</c>）整棵复制到 <paramref name="targetDirectory"/>，
    /// 返回复制的文件数，每 50 个文件报一次进度。
    /// 实例里两个目录都没有（刚建的空白实例）时返回 0，不抛异常。
    /// 空目录不复制（只统计文件）；junction / 符号链接跳过，避免目录环和跑出实例目录。
    /// </summary>
    public async Task<int> ExtractOverridesAsync(
        string targetDirectory,
        IProgress<ModpackImportProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(targetDirectory))
            throw new ArgumentException("targetDirectory 为空", nameof(targetDirectory));

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory));
        var gameDirectory = _gameDirectory;

        if (gameDirectory is null || !Directory.Exists(gameDirectory))
        {
            progress?.Report(ModpackImportProgress.Of(CopyStage, 100, "实例里没有 minecraft/ 或 .minecraft/，没东西可复制"));
            return 0;
        }

        var gameRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDirectory));

        // 自拷贝防护：目标就是游戏目录、或嵌在游戏目录里，会把源文件一半覆盖掉
        if (string.Equals(root, gameRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"目标目录就是实例的游戏目录本身，拒绝自己复制自己：{root}");
        if (IsUnder(root, gameRoot))
            throw new InvalidOperationException($"目标目录在实例的游戏目录里面，拒绝复制：{root} ⊂ {gameRoot}");
        if (IsUnder(gameRoot, root))
            throw new InvalidOperationException($"目标目录是实例游戏目录的上级，会覆盖源文件：{root} ⊃ {gameRoot}");

        ct.ThrowIfCancellationRequested();

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            // 不跟 junction / 符号链接：防目录环，也防顺着链接跑出实例目录
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true,
            MatchType = MatchType.Simple,
        };

        var files = Directory.EnumerateFiles(gameRoot, "*", options).ToList();
        var total = files.Count;

        LongPath.EnsureDirectory(root);
        if (total == 0)
        {
            progress?.Report(ModpackImportProgress.Of(CopyStage, 100, "游戏目录是空的"));
            return 0;
        }

        progress?.Report(ModpackImportProgress.Of(CopyStage, 0, $"共 {total} 个文件"));

        var written = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(gameRoot, file);
            if (Path.IsPathRooted(relative) ||
                relative.Equals("..", StringComparison.Ordinal) ||
                relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException($"实例内路径越界：{file}");

            var destination = LongPath.Combine(root, relative);
            if (!IsUnder(destination, root))
                throw new InvalidDataException($"复制目标越界：{file} → {destination}");

            LongPath.EnsureDirectory(Path.GetDirectoryName(destination)!);

            await using (var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            await using (var target = LongPath.CreateFile(destination))
                await source.CopyToAsync(target, ct).ConfigureAwait(false);

            written++;
            if (written % ProgressEvery == 0)
                progress?.Report(ModpackImportProgress.Of(CopyStage, written * 100.0 / total, $"{written} / {total}"));
        }

        progress?.Report(ModpackImportProgress.Of(CopyStage, 100, $"{written} 个文件"));
        return written;
    }

    /// <summary>全是内存态，没有句柄要放。</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>校验并规范化实例目录，失败时把原因讲明白。</summary>
    private static string ResolveInstanceDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("打开 MultiMC 实例失败：路径为空，需要传实例文件夹（含 mmc-pack.json 的那个目录）");

        string fullPath;
        try
        {
            fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException($"打开 MultiMC 实例失败：路径不合法 —— {path}", ex);
        }

        if (File.Exists(fullPath))
            throw new InvalidOperationException(
                $"打开 MultiMC 实例失败：{fullPath} 是文件。MultiMC / Prism 实例是文件夹，直接选实例目录，不要选导出的 .zip");

        if (!Directory.Exists(fullPath))
            throw new InvalidOperationException($"打开 MultiMC 实例失败：目录不存在 —— {fullPath}");

        string? packJsonPath;
        try
        {
            packJsonPath = FindFile(fullPath, PackJsonName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"打开 MultiMC 实例失败：读不了实例目录 —— {fullPath}（{ex.Message}）", ex);
        }

        if (packJsonPath is null)
            throw new InvalidOperationException(
                $"打开 MultiMC 实例失败：{fullPath} 里没有 {PackJsonName}，不是 MultiMC / Prism 实例目录（Modrinth / CurseForge 包请走 zip 那条路）");

        return fullPath;
    }

    /// <summary>
    /// 解析 mmc-pack.json 的 components。只留 uid + version（+ dependencyOnly）：
    /// 其余字段（formatVersion / cached / volatile / important）对映射没有用。
    /// </summary>
    private static List<Component> ParseComponents(string packJsonPath)
    {
        string json;
        try
        {
            json = File.ReadAllText(packJsonPath); // 走文本路径，顺带吃掉 BOM
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"读不了 {packJsonPath}：{ex.Message}", ex);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"mmc-pack.json 不是合法 JSON：{packJsonPath}", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"mmc-pack.json 根节点不是对象：{packJsonPath}");

            var components = new List<Component>();
            if (!root.TryGetProperty("components", out var array) || array.ValueKind != JsonValueKind.Array)
                return components;

            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                var uid = GetString(item, "uid");
                var version = GetString(item, "version");
                if (string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(version)) continue;

                var dependencyOnly = item.TryGetProperty("dependencyOnly", out var flag) &&
                                     flag.ValueKind == JsonValueKind.True;

                components.Add(new Component(uid.Trim(), version.Trim(), dependencyOnly));
            }

            return components;
        }
    }

    /// <summary>
    /// 取组件版本。<paramref name="uids"/> 按优先级排列。
    /// 优先取「用户显式选中」的组件（dependencyOnly=false）：Prism 装 1.20.1 NeoForge 时会把
    /// net.minecraftforge 当作依赖组件一起写进 mmc-pack.json（dependencyOnly=true），不分主次的话
    /// ForgeVersion 会抢先命中，最后装成 Forge 而不是 NeoForge。
    /// </summary>
    private static string? PickVersion(IReadOnlyList<Component> components, params string[] uids)
    {
        foreach (var uid in uids)
        {
            for (var i = components.Count - 1; i >= 0; i--)
            {
                var component = components[i];
                if (!component.DependencyOnly && component.Uid.Equals(uid, StringComparison.OrdinalIgnoreCase))
                    return component.Version;
            }
        }

        foreach (var uid in uids)
        {
            for (var i = components.Count - 1; i >= 0; i--)
            {
                var component = components[i];
                if (component.Uid.Equals(uid, StringComparison.OrdinalIgnoreCase))
                    return component.Version;
            }
        }

        return null;
    }

    /// <summary>components → modrinth.index.json 那套 dependencies 键（minecraft / forge / neoforge / fabric-loader / quilt-loader）。</summary>
    private static IReadOnlyDictionary<string, string> BuildDependencies(IReadOnlyList<Component> components)
    {
        var dependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Put(string key, string? version)
        {
            if (!string.IsNullOrWhiteSpace(version)) dependencies[key] = version;
        }

        Put(MinecraftDependency, PickVersion(components, MinecraftComponent));

        var neoForge = PickVersion(components, NeoForgeComponent, NeoForgeNeoformComponent);
        Put(NeoForgeDependency, neoForge);
        // NeoForge 实例里可能同时有 net.minecraftforge 依赖组件；两个都写会让 ModpackInstaller 先装 Forge
        if (neoForge is null)
            Put(ForgeDependency, PickVersion(components, ForgeComponent));

        Put(FabricDependency, PickVersion(components, FabricComponent));
        Put(QuiltDependency, PickVersion(components, QuiltComponent));

        return dependencies;
    }

    /// <summary>
    /// 解析 instance.cfg：MultiMC 用 INI 风格，一行一个 <c>key=value</c>，<c>#</c> 注释，<c>[段]</c> 忽略。
    /// 只需要 name，但整份读进来方便以后取 iconKey / InstanceType。
    /// </summary>
    private static Dictionary<string, string> ParseInstanceConfig(string? configPath)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (configPath is null || !File.Exists(configPath)) return values;

        string[] lines;
        try
        {
            lines = File.ReadAllLines(configPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return values; // 读不了就用文件夹名兜底
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == ';' || line[0] == '[') continue;

            var separator = line.IndexOf('=');
            if (separator <= 0) continue;

            var key = line[..separator].Trim();
            if (key.Length == 0) continue;

            var value = line[(separator + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                value = value[1..^1];

            values[key] = value;
        }

        return values;
    }

    /// <summary>游戏目录：minecraft/ 优先，其次 .minecraft/，再兜底按大小写不敏感扫一层。</summary>
    private static string? FindGameDirectory(string instanceDirectory)
    {
        foreach (var name in GameDirectoryNames)
        {
            var direct = Path.Combine(instanceDirectory, name);
            if (Directory.Exists(direct)) return direct;
        }

        try
        {
            var subdirectories = Directory.EnumerateDirectories(instanceDirectory).ToList();
            foreach (var name in GameDirectoryNames)
            {
                var match = subdirectories.FirstOrDefault(d =>
                    Path.GetFileName(d).Equals(name, StringComparison.OrdinalIgnoreCase));
                if (match is not null) return match;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 扫不动就当没有游戏目录，ExtractOverridesAsync 会报「没东西可复制」
        }

        return null;
    }

    /// <summary>先按原名找，找不到再按大小写不敏感扫一层（实例可能是从 Linux 拷过来的）。</summary>
    private static string? FindFile(string directory, string fileName)
    {
        var direct = Path.Combine(directory, fileName);
        if (File.Exists(direct)) return direct;

        return Directory.EnumerateFiles(directory)
            .FirstOrDefault(f => Path.GetFileName(f).Equals(fileName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary><paramref name="fullPath"/> 是否严格位于 <paramref name="root"/> 之下（不含相等）。两侧都传绝对路径。</summary>
    private static bool IsUnder(string fullPath, string root)
    {
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private readonly record struct Component(string Uid, string Version, bool DependencyOnly);
}
