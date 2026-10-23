using System.Security.Cryptography;

namespace Launcher.Infrastructure.Update;

/// <summary>将可信内嵌更新器写入独立缓存，按内容哈希复用；不覆盖运行中的旧版本。</summary>
public static class EmbeddedUpdater
{
    public static string Extract(Stream payload, string cacheRoot)
    {
        using var memory = new MemoryStream();
        payload.CopyTo(memory);
        var bytes = memory.ToArray();
        if (bytes.Length == 0) throw new InvalidDataException("安装包中的更新器为空，请重新下载启动器。");
        var hash = SHA256.HashData(bytes);
        var directory = Path.Combine(cacheRoot, "update-helper", Convert.ToHexStringLower(hash));
        var path = Path.Combine(directory, "mclupd.exe");
        if (File.Exists(path))
        {
            using var existing = File.OpenRead(path);
            if (existing.Length == bytes.Length && SHA256.HashData(existing).AsSpan().SequenceEqual(hash)) return path;
        }
        Directory.CreateDirectory(directory);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return path;
    }
}
