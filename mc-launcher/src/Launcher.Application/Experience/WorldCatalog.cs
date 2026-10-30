using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Launcher.Application.Experience;

public sealed record WorldEntry(string Folder, string Name, string Version, string Mode, DateTimeOffset? LastPlayed, long Bytes, string? IconPath, string Notice)
{
    public string Summary => $"{Version} · {Mode} · {Bytes / 1048576d:F1} MB" + (LastPlayed is {} date ? $" · 最近游玩 {date.LocalDateTime:yyyy-MM-dd HH:mm}" : "");
}
public sealed record WorldBackup(string Id, string Folder, string Name, DateTimeOffset CreatedAt, IReadOnlyList<SnapshotFile> Files)
{
    public string Summary => $"{CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm} · {Files.Sum(f => f.Bytes) / 1048576d:F1} MB";
}

/// <summary>一个世界一个备份；恢复前验证完整归档，再替换目录，保留其他世界。</summary>
public sealed class WorldCatalog
{
    private readonly string _saves;
    private readonly string _backups;
    public WorldCatalog(string dataRoot, string gameRoot, string instanceId)
    {
        _saves = ExperienceFiles.Child(ExperienceFiles.Child(Path.Combine(gameRoot, "instances"), instanceId), ".minecraft/saves");
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(gameRoot) + "\n" + instanceId)));
        _backups = Path.Combine(dataRoot, "world-backups", key);
    }
    private string WorldPath(string folder)
    {
        if (folder != Path.GetFileName(folder) || folder is "." or "..") throw new InvalidDataException("无效的世界目录。");
        var path = ExperienceFiles.Child(_saves, folder); ExperienceFiles.RejectLinks(path); return path;
    }
    public IReadOnlyList<WorldEntry> List(CancellationToken ct = default)
    {
        ExperienceFiles.RejectLinks(_saves);
        var result = new List<WorldEntry>();
        foreach (var path in Directory.Exists(_saves) ? Directory.EnumerateDirectories(_saves) : [])
        {
            ct.ThrowIfCancellationRequested();
            var folder = Path.GetFileName(path);
            if (folder.StartsWith(".fangyu-", StringComparison.Ordinal)) continue;
            try
            {
                ExperienceFiles.RejectLinks(path);
                var level = Path.Combine(path, "level.dat");
                if (!File.Exists(level) && !File.Exists(Path.Combine(path, "level.dat_old"))) continue;
                var info = new WorldEntry(folder, folder, "版本未知", "模式未知", null, 0, null, "");
                try { info = ReadLevel(level, info); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or OverflowException)
                { info = info with { Notice = "世界信息无法读取，仍可备份。" }; }
                long bytes = 0;
                foreach (var file in ExperienceFiles.Files(path)) { ct.ThrowIfCancellationRequested(); bytes += new FileInfo(file).Length; }
                var icon = Path.Combine(path, "icon.png");
                result.Add(info with { Bytes = bytes, IconPath = File.Exists(icon) && new FileInfo(icon).Length <= 1048576 ? icon : null });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { result.Add(new(folder, folder, "版本未知", "模式未知", null, 0, null, "目录不可读取或含链接，请检查后再备份。")); }
        }
        ExperienceFiles.RejectLinks(_backups);
        if (Directory.Exists(_backups))
            foreach (var backup in Directory.EnumerateFiles(_backups, "*.json").Select(ExperienceFiles.Read<WorldBackup>).OfType<WorldBackup>().OrderByDescending(b => b.CreatedAt))
            {
                if (result.Any(w => w.Folder == backup.Folder)) continue;
                if (Directory.Exists(WorldPath(backup.Folder))) continue;
                result.Add(new(backup.Folder, backup.Name, "本地世界已移除", "可从备份恢复", null, 0, null, "选择下方备份，可重新恢复此世界。"));
            }
        return result.OrderByDescending(w => w.LastPlayed).ThenBy(w => w.Name).ToArray();
    }
    public IReadOnlyList<WorldBackup> Backups(string folder)
    {
        WorldPath(folder);
        ExperienceFiles.RejectLinks(_backups);
        return Directory.Exists(_backups) ? Directory.EnumerateFiles(_backups, "*.json")
            .Select(ExperienceFiles.Read<WorldBackup>).OfType<WorldBackup>().Where(b => b.Folder == folder)
            .OrderByDescending(b => b.CreatedAt).ToArray() : [];
    }
    public async Task<WorldBackup> BackupAsync(string folder, string name, CancellationToken ct = default)
    {
        var world = WorldPath(folder);
        if (!Directory.Exists(world)) throw new DirectoryNotFoundException("世界已不存在，请刷新列表。");
        ExperienceFiles.RejectLinks(_backups); Directory.CreateDirectory(_backups);
        var id = Guid.NewGuid().ToString("N"); var zipPath = Path.Combine(_backups, id + ".zip");
        var temporary = zipPath + ".tmp";
        try
        {
            var files = new List<SnapshotFile>();
            using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
                foreach (var file in ExperienceFiles.Files(world))
                {
                    ct.ThrowIfCancellationRequested();
                    if (Path.GetFileName(file) == "session.lock") continue;
                    var relative = Path.GetRelativePath(world, file).Replace('\\', '/');
                    await using var input = File.OpenRead(file);
                    var entry = zip.CreateEntry(relative, CompressionLevel.Fastest);
                    await using (var output = entry.Open()) await input.CopyToAsync(output, ct);
                    input.Position = 0;
                    files.Add(new(relative, input.Length, Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct))));
                }
            ct.ThrowIfCancellationRequested();
            var backup = new WorldBackup(id, folder, name, DateTimeOffset.UtcNow, files);
            File.Move(temporary, zipPath);
            ExperienceFiles.Write(Path.Combine(_backups, id + ".json"), backup);
            return backup;
        }
        catch { File.Delete(zipPath); throw; }
        finally { File.Delete(temporary); }
    }
    public async Task RestoreAsync(string backupId, CancellationToken ct = default)
    {
        if (!Guid.TryParseExact(backupId, "N", out _)) throw new InvalidDataException("无效的备份编号。");
        ExperienceFiles.RejectLinks(_backups);
        var backup = ExperienceFiles.Read<WorldBackup>(Path.Combine(_backups, backupId + ".json")) ?? throw new InvalidDataException("备份记录不存在。");
        if (backup.Id != backupId || backup.Files.Count == 0) throw new InvalidDataException("备份记录无效。");
        var world = WorldPath(backup.Folder);
        Directory.CreateDirectory(_saves);
        var stage = Path.Combine(_saves, ".fangyu-restore-" + Guid.NewGuid().ToString("N"));
        var previous = stage + "-previous";
        Directory.CreateDirectory(stage);
        try
        {
            using var zip = ZipFile.OpenRead(Path.Combine(_backups, backupId + ".zip"));
            var paths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var file in backup.Files)
            {
                ct.ThrowIfCancellationRequested();
                var target = ExperienceFiles.Child(stage, file.Path);
                if (!paths.Add(target)) throw new InvalidDataException("备份有重复文件。");
                var entry = zip.GetEntry(file.Path) ?? throw new InvalidDataException("备份缺少文件。");
                if (entry.Length != file.Bytes) throw new InvalidDataException("备份文件大小不符。");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using (var input = entry.Open()) await using (var output = File.Create(target)) await input.CopyToAsync(output, ct);
                await using var check = File.OpenRead(target);
                if (!Convert.ToHexStringLower(await SHA256.HashDataAsync(check, ct)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("备份校验失败，当前世界未改动。");
            }
            // 归档验证完成后才保存当前状态。取消或失败时保留世界原状。
            if (Directory.Exists(world)) await BackupAsync(backup.Folder, "恢复前自动备份：" + backup.Name, ct);
            ct.ThrowIfCancellationRequested(); WorldPath(backup.Folder);
            if (Directory.Exists(world)) Directory.Move(world, previous);
            try { Directory.Move(stage, world); }
            catch { if (Directory.Exists(previous)) Directory.Move(previous, world); throw; }
            ExperienceFiles.DeleteTree(previous, _saves);
        }
        finally { ExperienceFiles.DeleteTree(stage, _saves); }
    }
    public string GetWorldPath(string folder) => WorldPath(folder);
    public void DeleteBackup(string backupId)
    {
        if (!Guid.TryParseExact(backupId, "N", out _)) throw new InvalidDataException("无效的备份编号。");
        ExperienceFiles.RejectLinks(_backups);
        File.Delete(Path.Combine(_backups, backupId + ".zip")); File.Delete(Path.Combine(_backups, backupId + ".json"));
    }

    // level.dat 是 gzip 压缩的 NBT。只读取界面所需标量，其余标签按格式跳过。
    private static WorldEntry ReadLevel(string path, WorldEntry world)
    {
        ExperienceFiles.RejectLinks(path);
        using var input = File.OpenRead(path); using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var memory = new MemoryStream();
        var buffer = new byte[8192]; int count;
        while ((count = gzip.Read(buffer)) != 0)
        { if (memory.Length + count > 16 * 1048576) throw new InvalidDataException("世界信息过大。"); memory.Write(buffer, 0, count); }
        memory.Position = 0; using var reader = new BinaryReader(memory, Encoding.UTF8);
        short I16() => System.Buffers.Binary.BinaryPrimitives.ReadInt16BigEndian(reader.ReadBytes(2));
        int I32() => System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(reader.ReadBytes(4));
        long I64() => System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(reader.ReadBytes(8));
        string Text() { var length = (ushort)I16(); var value = reader.ReadBytes(length); if (value.Length != length) throw new EndOfStreamException(); return Encoding.UTF8.GetString(value); }
        void Skip(long bytes) { if (bytes < 0 || bytes > memory.Length - memory.Position) throw new InvalidDataException("NBT 长度无效。"); memory.Position += bytes; }
        var nodes = 0;
        void Tag(byte type, string key, string parent, int depth)
        {
            if (depth > 64 || ++nodes > 200000) throw new InvalidDataException("NBT 嵌套过深。");
            switch (type)
            {
                case 1: var b = reader.ReadByte(); if (parent == "Data" && key == "hardcore" && b != 0) world = world with { Mode = "极限模式" }; break;
                case 2: Skip(2); break;
                case 3: var i = I32(); if (parent == "Data" && key == "GameType" && world.Mode != "极限模式") world = world with { Mode = i switch { 0 => "生存", 1 => "创造", 2 => "冒险", 3 => "旁观", _ => "模式未知" } }; break;
                case 4: var l = I64(); if (parent == "Data" && key == "LastPlayed") world = world with { LastPlayed = DateTimeOffset.FromUnixTimeMilliseconds(l) }; break;
                case 5: Skip(4); break;
                case 6: Skip(8); break;
                case 7: Skip(I32()); break;
                case 8: var s = Text(); if (parent == "Data" && key == "LevelName") world = world with { Name = s }; if (parent == "Version" && key == "Name") world = world with { Version = s }; break;
                case 9: var element = reader.ReadByte(); var length = I32(); if (length < 0 || length > 200000 || element == 0 && length != 0) throw new InvalidDataException("NBT 列表无效。"); for (var n = 0; n < length; n++) Tag(element, "", "", depth + 1); break;
                case 10: byte child; while ((child = reader.ReadByte()) != 0) Tag(child, Text(), key, depth + 1); break;
                case 11: Skip((long)I32() * 4); break;
                case 12: Skip((long)I32() * 8); break;
                default: throw new InvalidDataException("NBT 标签无效。");
            }
        }
        var root = reader.ReadByte(); if (root != 10) throw new InvalidDataException("NBT 根标签无效。"); Tag(root, Text(), "", 0);
        return world;
    }
}
