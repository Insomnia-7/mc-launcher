using System.IO.Compression;
using Launcher.Application.Experience;
using Launcher.Application.Install;

namespace Launcher.Infrastructure.Modpacks;

internal static class PackIconReader
{
    private static readonly string[] Names = ["icon.png", "pack.png", "icon.jpg", "icon.jpeg", "icon.webp"];

    public static async Task<byte[]?> FromArchiveAsync(ZipArchive archive, string manifestName, string overrides, CancellationToken ct)
    {
        var manifest = archive.Entries.First(e => e.Name.Equals(manifestName, StringComparison.OrdinalIgnoreCase));
        var prefix = manifest.FullName[..^manifest.Name.Length];
        // Check only pack-level artwork, never a mod's or resource pack's embedded pack.png.
        foreach (var directory in new[] { "", "client-overrides/", overrides.TrimEnd('/') + "/" }.Distinct())
            foreach (var name in Names)
            {
                ct.ThrowIfCancellationRequested();
                var entry = archive.Entries.FirstOrDefault(e => e.FullName.Equals(prefix + directory + name, StringComparison.OrdinalIgnoreCase));
                if (entry is null || entry.Length > ModpackIcon.MaxBytes) continue;
                await using var stream = entry.Open();
                if (await ReadAsync(stream, ct).ConfigureAwait(false) is {} bytes) return bytes;
            }
        return null;
    }

    public static async Task<byte[]?> FromDirectoryAsync(string root, string? iconKey, CancellationToken ct)
    {
        var names = Names.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(iconKey) && iconKey.IndexOfAny(['/', '\\', ':']) < 0 && iconKey is not "." and not "..")
            names = new[] { iconKey + ".png", "icons/" + iconKey + ".png" }.Concat(names);
        foreach (var relative in names.Concat(Names.Select(n => "minecraft/" + n)).Concat(Names.Select(n => ".minecraft/" + n)))
        {
            ct.ThrowIfCancellationRequested();
            var path = ExperienceFiles.Child(root, relative);
            if (!File.Exists(path)) continue;
            ExperienceFiles.RejectLinks(path);
            if (new FileInfo(path).Length > ModpackIcon.MaxBytes) continue;
            await using var stream = File.OpenRead(path);
            if (await ReadAsync(stream, ct).ConfigureAwait(false) is {} bytes) return bytes;
        }
        return null;
    }

    private static async Task<byte[]?> ReadAsync(Stream stream, CancellationToken ct)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (output.Length + count > ModpackIcon.MaxBytes) return null;
            output.Write(buffer, 0, count);
        }
        var bytes = output.ToArray(); return ModpackIcon.IsImage(bytes) ? bytes : null;
    }
}
