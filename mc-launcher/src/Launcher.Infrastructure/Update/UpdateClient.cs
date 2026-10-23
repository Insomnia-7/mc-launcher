using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Launcher.Application.Update;
using Launcher.Infrastructure.Http;

namespace Launcher.Infrastructure.Update;

/// <summary>
/// 更新清单的取用与文件暂存。来源支持三种：http(s) 地址、本地 .json 文件、本地目录
/// （目录时自动拼 update.json）—— 测试和「把启动器放在 U 盘上手动更新」都靠后两种。
/// </summary>
public sealed class UpdateClient(HttpClient http, UpdateSignatureVerifier? signatureVerifier = null) : IUpdateClient
{
    private readonly UpdateSignatureVerifier _signatures = signatureVerifier ?? new();
    public const string ManifestFileName = "update.json";
    public const string EnvironmentVariable = "MCLAUNCHER_UPDATE_URL";


    public static HttpClient CreateHttpClient() =>
        HttpClientFactory.Create("MCLauncher-Updater/1.0", TimeSpan.FromSeconds(60));

    public async Task<UpdateCheckResult> CheckAsync(string currentVersion, string? location = null, CancellationToken ct = default)
    {
        var resolved = ResolveLocation(location);
        if (resolved is null)
            return new UpdateCheckResult(null, Reason: $"没有配置更新地址（环境变量 {EnvironmentVariable} 或 settings.json 的 updateUrl）", CurrentVersion: currentVersion, Failed: true);

        UpdateManifest manifest;
        try
        {
            var (document, signature) = await ReadSignedManifestAsync(resolved, ct).ConfigureAwait(false);
            manifest = _signatures.Verify(document, signature);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or UpdateManifestException or UnauthorizedAccessException)
        {
            return new UpdateCheckResult(null, resolved, $"更新验证失败：{ex.Message}", currentVersion, Failed: true);
        }

        if (!UpdateVersion.IsNewer(manifest.Version, currentVersion))
            return new UpdateCheckResult(null, resolved, $"已是最新（{currentVersion}）", currentVersion);

        return new UpdateCheckResult(manifest, resolved, null, currentVersion);
    }

    public async Task<UpdateStaging> StageAsync(
        UpdateManifest manifest,
        string? location,
        string targetDirectory,
        string dataDirectory,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        manifest = _signatures.Reverify(manifest);
        var resolved = ResolveLocation(location);
        var stagingDirectory = Path.Combine(dataDirectory, "update", manifest.Version);
        Directory.CreateDirectory(stagingDirectory);

        var downloaded = 0;
        var skipped = 0;
        var done = 0L;

        foreach (var file in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();

            var stagePath = Path.Combine(stagingDirectory, file.Path.Replace('/', Path.DirectorySeparatorChar));
            var targetPath = Path.Combine(targetDirectory, file.Path.Replace('/', Path.DirectorySeparatorChar));

            if (await MatchesAsync(stagePath, file, ct).ConfigureAwait(false) ||
                await MatchesAsync(targetPath, file, ct).ConfigureAwait(false))
            {
                skipped++;
                done += Math.Max(0, file.Size);
                progress?.Report(new UpdateProgress("暂存", Percent(done, manifest.TotalBytes), $"跳过 {file.Path}"));
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(stagePath)!);

            var url = ResolveFileUrl(resolved, file.Url);
            progress?.Report(new UpdateProgress("下载", Percent(done, manifest.TotalBytes), file.Path));

            var temporary = stagePath + ".part";

            // 清单可以在本地目录里（测试 / U 盘更新），这时不需要走 HTTP
            if (Uri.TryCreate(url, UriKind.Absolute, out var fileUri) && fileUri.IsFile)
            {
                File.Copy(fileUri.LocalPath, temporary, overwrite: true);
            }
            else
            {
                await using var source = await http.GetStreamAsync(url, ct).ConfigureAwait(false);
                await using var destination = File.Create(temporary);
                await source.CopyToAsync(destination, 81920, ct).ConfigureAwait(false);
            }

            if (!await MatchesAsync(temporary, file, ct).ConfigureAwait(false))
            {
                var actual = await Sha256Async(temporary, ct).ConfigureAwait(false);
                File.Delete(temporary);
                throw new InvalidOperationException(
                    $"文件校验不通过：{file.Path}（期望 sha256 {Short(file.Sha256)}，实际 {Short(actual)}）");
            }

            File.Move(temporary, stagePath, overwrite: true);
            downloaded++;
            done += Math.Max(0, file.Size);
            progress?.Report(new UpdateProgress("下载", Percent(done, manifest.TotalBytes), file.Path));
        }

        // 保留签名针对的原始字节，独立 updater 会再次验证，不能重新序列化。
        var manifestPath = Path.Combine(stagingDirectory, ManifestFileName);
        await File.WriteAllBytesAsync(manifestPath, Convert.FromBase64String(manifest.SignatureProof!.ManifestBase64), ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(manifestPath + ".sig", manifest.SignatureProof.SignatureJson, new UTF8Encoding(false), ct).ConfigureAwait(false);

        progress?.Report(new UpdateProgress("就绪", 100, $"新版本 {manifest.Version} 已准备好"));
        return new UpdateStaging(stagingDirectory, manifest, downloaded, skipped);
    }

    /// <summary>环境变量优先，其次调用方给的地址；两者都没有返回 null。</summary>
    private static string? ResolveLocation(string? location)
    {
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } fromEnvironment)
            return fromEnvironment;

        return string.IsNullOrWhiteSpace(location) ? null : location;
    }

    private async Task<(byte[] Document, byte[] Signature)> ReadSignedManifestAsync(string location, CancellationToken ct)
    {
        if (Directory.Exists(location)) location = Path.Combine(location, ManifestFileName);
        if (File.Exists(location))
        {
            var signaturePath = location + ".sig";
            if (!File.Exists(signaturePath))
                throw new UpdateManifestException($"更新清单缺少发布者签名（{Path.GetFileName(signaturePath)}），已停止更新。");
            return (await ReadBoundedAsync(File.OpenRead(location), UpdateSignatureVerifier.MaxManifestBytes, ct),
                await ReadBoundedAsync(File.OpenRead(signaturePath), UpdateSignatureVerifier.MaxSignatureBytes, ct));
        }

        if (!Uri.TryCreate(location, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            throw new InvalidOperationException("不认识的更新地址。");

        var signatureUri = new UriBuilder(uri) { Path = uri.AbsolutePath + ".sig" }.Uri;
        var document = await ReadBoundedAsync(await http.GetStreamAsync(uri, ct).ConfigureAwait(false), UpdateSignatureVerifier.MaxManifestBytes, ct);
        byte[] signature;
        try
        {
            signature = await ReadBoundedAsync(await http.GetStreamAsync(signatureUri, ct).ConfigureAwait(false), UpdateSignatureVerifier.MaxSignatureBytes, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateManifestException($"更新源没有提供 update.json.sig 签名文件（{ex.Message}），已停止更新。", ex);
        }

        return (document, signature);
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream source, int limit, CancellationToken ct)
    {
        await using (source)
        {
            using var result = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                if (result.Length + count > limit) throw new UpdateManifestException("更新清单或签名超过大小限制。");
                result.Write(buffer, 0, count);
            }
            return result.ToArray();
        }
    }

    /// <summary>文件地址可以是绝对的 http 地址，也可以是相对清单位置的路径。</summary>
    private static string ResolveFileUrl(string? manifestLocation, string fileUrl)
    {
        if (Uri.TryCreate(fileUrl, UriKind.Absolute, out var absolute) &&
            (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
            return absolute.ToString();

        if (manifestLocation is null)
            throw new InvalidOperationException($"文件 {fileUrl} 用的是相对地址，但清单位置未知");

        if (Directory.Exists(manifestLocation))
            return new Uri(Path.Combine(manifestLocation, fileUrl)).AbsoluteUri;

        if (File.Exists(manifestLocation))
            return new Uri(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifestLocation))!, fileUrl)).AbsoluteUri;

        return new Uri(new Uri(manifestLocation), fileUrl).ToString();
    }

    private static async Task<bool> MatchesAsync(string path, UpdateFile file, CancellationToken ct)
    {
        if (!File.Exists(path))
            return false;

        var info = new FileInfo(path);
        if (info.Length != file.Size)
            return false;


        var actual = await Sha256Async(path, ct).ConfigureAwait(false);
        return string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static double Percent(long done, long total) =>
        total <= 0 ? 0 : Math.Clamp(done * 100d / total, 0, 100);

    private static string Short(string? sha) =>
        string.IsNullOrEmpty(sha) ? "(空)" : sha.Length <= 12 ? sha : sha[..12] + "…";
}
