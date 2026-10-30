using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Launcher.Application.Accounts;

namespace Launcher.Infrastructure.Accounts;

public sealed class MinecraftAppearanceClient(HttpClient http) : IPlayerAppearanceClient
{
    private const string Endpoint = "https://api.minecraftservices.com/minecraft/profile";
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token, HttpContent? content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, Endpoint + path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var code = response.StatusCode; response.Dispose();
            throw new HttpRequestException(code == System.Net.HttpStatusCode.Unauthorized ? "登录已过期，请重新验证账号。" : $"Minecraft 外观服务返回 HTTP {(int)code}，外观尚未修改成功。", null, code);
        }
        return response;
    }
    public async Task<AppearanceProfile> GetAsync(string token, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "", token, null, ct);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = json.RootElement;
        string? skin = null; var model = "classic";
        if (root.TryGetProperty("skins", out var skins))
            foreach (var item in skins.EnumerateArray())
                if (item.GetProperty("state").GetString() == "ACTIVE")
                { skin = item.GetProperty("url").GetString(); model = item.GetProperty("variant").GetString()?.ToLowerInvariant() ?? "classic"; break; }
        var capes = new List<OwnedCape>();
        if (root.TryGetProperty("capes", out var values))
            foreach (var c in values.EnumerateArray()) capes.Add(new(c.GetProperty("id").GetString()!, c.TryGetProperty("alias", out var alias) ? alias.GetString() ?? "披风" : "披风", c.GetProperty("url").GetString()!, c.GetProperty("state").GetString() == "ACTIVE"));
        return new(root.GetProperty("id").GetString()!, root.GetProperty("name").GetString()!, skin, model, capes);
    }
    public async Task UploadSkinAsync(string token, byte[] png, string model, CancellationToken ct = default)
    {
        WardrobeStore.ValidatePng(png);
        if (model is not ("classic" or "slim")) throw new InvalidDataException("皮肤模型无效。");
        using var body = new MultipartFormDataContent();
        body.Add(new StringContent(model), "variant");
        var file = new ByteArrayContent(png); file.Headers.ContentType = new("image/png"); body.Add(file, "file", "skin.png");
        using var response = await SendAsync(HttpMethod.Post, "/skins", token, body, ct);
    }
    public async Task ResetSkinAsync(string token, CancellationToken ct = default) { using var r = await SendAsync(HttpMethod.Delete, "/skins/active", token, null, ct); }
    public async Task SetCapeAsync(string token, string? capeId, CancellationToken ct = default)
    {
        using var body = capeId is null ? null : JsonContent.Create(new { capeId });
        using var response = await SendAsync(capeId is null ? HttpMethod.Delete : HttpMethod.Put, "/capes/active", token, body, ct);
    }
    public async Task<byte[]> GetTextureAsync(string url, CancellationToken ct = default)
    {
        var uri = new Uri(url);
        if (uri.Host != "textures.minecraft.net") throw new InvalidDataException("纹理地址不是 Minecraft 纹理服务。");
        uri = new UriBuilder(uri) { Scheme = "https", Port = -1 }.Uri;
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct); response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        using var result = new MemoryStream(); var buffer = new byte[16384]; int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0) { if (result.Length + read > 2097152) throw new InvalidDataException("纹理文件过大。"); await result.WriteAsync(buffer.AsMemory(0, read), ct); }
        return result.ToArray();
    }
}
