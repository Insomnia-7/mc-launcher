using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Launcher.Application.Mods;

namespace Launcher.Infrastructure.Meta;

/// <summary>Optional Chinese descriptions and original project icons, cached independently of file downloads.</summary>
public sealed class ResourcePresentationClient(HttpClient http,string cacheDirectory) : IResourcePresentationClient
{
    private readonly SemaphoreSlim _icons = new(4);
    private static string Key(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public async Task<IReadOnlyList<ResourceTranslation>> GetTranslationsAsync(string source,IReadOnlyList<string> ids,CancellationToken ct=default)
    {
        if(ids.Count==0) return [];
        Directory.CreateDirectory(cacheDirectory);
        var result=new List<ResourceTranslation>(); var missing=new List<string>();
        foreach(var id in ids.Distinct())
        {
            var path=Path.Combine(cacheDirectory,"zh-"+Key(source+id)+".json");
            try
            {
                if(File.Exists(path) && DateTime.UtcNow-File.GetLastWriteTimeUtc(path)<TimeSpan.FromDays(7))
                {
                    var cached=JsonSerializer.Deserialize<ResourceTranslation>(await File.ReadAllTextAsync(path,ct));
                    if(cached is not null) { result.Add(cached); continue; }
                }
            }
            catch(Exception ex) when(ex is IOException or JsonException or UnauthorizedAccessException) { }
            missing.Add(id);
        }
        if(missing.Count==0) return result;
        var cf=source.Equals("CurseForge",StringComparison.OrdinalIgnoreCase);
        object payload=cf ? new {modids=missing.Select(int.Parse).ToArray()} : new {project_ids=missing.ToArray()};
        using var response=await http.PostAsJsonAsync("https://mod.mcimirror.top/translate/"+(cf?"curseforge":"modrinth"),payload,ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        foreach(var item in json.RootElement.EnumerateArray())
        {
            var id=cf?item.GetProperty("modid").ToString():item.GetProperty("project_id").GetString()!;
            if(!missing.Contains(id)) continue;
            var translated=item.GetProperty("translated").GetString()??"";
            if(!ResourceChinese.HasChinese(translated)) continue;
            var value=new ResourceTranslation(id,item.GetProperty("original").GetString()??"",translated);
            result.Add(value);
            try { await File.WriteAllTextAsync(Path.Combine(cacheDirectory,"zh-"+Key(source+id)+".json"),JsonSerializer.Serialize(value),ct).ConfigureAwait(false); }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
        }
        return result;
    }

    public async Task<byte[]?> GetIconAsync(string url,CancellationToken ct=default)
    {
        if(!Uri.TryCreate(url,UriKind.Absolute,out var uri) || uri.Scheme!="https") return null;
        await _icons.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path=Path.Combine(cacheDirectory,"icon-"+Key(url));
            if(File.Exists(path) && DateTime.UtcNow-File.GetLastWriteTimeUtc(path)<TimeSpan.FromDays(30))
                return await File.ReadAllBytesAsync(path,ct).ConfigureAwait(false);
            using var response=await http.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            const int limit=2*1024*1024;
            if(response.Content.Headers.ContentLength>limit) return null;
            await using var stream=await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var bytes=new MemoryStream(); var buffer=new byte[8192]; int read;
            while((read=await stream.ReadAsync(buffer,ct).ConfigureAwait(false))>0)
            {
                if(bytes.Length+read>limit) return null;
                bytes.Write(buffer,0,read);
            }
            var data=bytes.ToArray();
            try { Directory.CreateDirectory(cacheDirectory); await File.WriteAllBytesAsync(path,data,ct).ConfigureAwait(false); }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
            return data;
        }
        finally { _icons.Release(); }
    }
}
