using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Launcher.Infrastructure.Horizon;

public sealed partial class HorizonClient
{
    public async Task<byte[]> OriginalVersionManifestAsync(HorizonBuild build,string minecraftDirectory,CancellationToken ct)
    {
        using var research=JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(build.Directory,"research-lock.json"),ct).ConfigureAwait(false));
        var game=research.RootElement.GetProperty("minecraft");
        var version=game.GetProperty("version").GetString();
        var sha1=game.GetProperty("manifest_sha1").GetString()??"";
        if(version!=build.Minecraft||!Regex.IsMatch(sha1,"^[a-f0-9]{40}$"))throw new InvalidDataException("Horizon 构建的原版版本校验信息不一致。");
        var path=Path.Combine(minecraftDirectory,"versions",SafeId(version!),version+".json");
        if(File.Exists(path))
        {
            var cached=await File.ReadAllBytesAsync(path,ct).ConfigureAwait(false);
            if(Convert.ToHexStringLower(SHA1.HashData(cached))==sha1)return cached;
        }
        // The preparer validates original bytes, including unknown JSON fields and whitespace.
        // Pin the exact Mojang package accepted by this build instead of reserializing our model.
        var bytes=await http.GetByteArrayAsync($"https://piston-meta.mojang.com/v1/packages/{sha1}/{version}.json",ct).ConfigureAwait(false);
        if(Convert.ToHexStringLower(SHA1.HashData(bytes))!=sha1)throw new InvalidDataException("Mojang 原版版本信息校验失败，未开始 Rust 转换。");
        using var json=JsonDocument.Parse(bytes);
        if(json.RootElement.GetProperty("id").GetString()!=version)throw new InvalidDataException("Mojang 原版版本信息与 Rust 构建不匹配。");
        return bytes;
    }
}
