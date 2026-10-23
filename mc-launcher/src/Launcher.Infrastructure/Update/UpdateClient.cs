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
public sealed class UpdateClient(HttpClient http) : IUpdateClient
{
    public const string ManifestFileName = "update.json";
    public const string EnvironmentVariable = "MCLAUNCHER_UPDATE_URL";

    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static HttpClient CreateHttpClient() =>
        HttpClientFactory.Create("MCLauncher-Updater/1.0", TimeSpan.FromSeconds(60));

    public async Task<UpdateCheckResult> CheckAsync(string currentVersion, string? location = null, CancellationToken ct = default)
    {
        var resolved = ResolveLocation(location);
        if (resolved is null)
            return new UpdateCheckResult(null, Reason: $"没有配置更新地址（环境变量 {EnvironmentVariable} 或 settings.json 的 updateUrl）", CurrentVersion: currentVersion);

        string json;
        try
        {
            json = await ReadManifestTextAsync(resolved, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            return new UpdateCheckResult(null, resolved, $"读取更新清单失败：{ex.Message}", currentVersion);
        }

        UpdateManifest manifest;
        try
        {
            manifest = UpdateManifestParser.Parse(json);
        }
        catch (UpdateManifestException ex)
        {
            return new UpdateCheckResult(null, resolved, ex.Message, currentVersion);
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

        // updater 只读这份，不再关心最初的来源是 http 还是本地目录
        var manifestPath = Path.Combine(stagingDirectory, ManifestFileName);
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(manifest, ManifestJson),
            Encoding.UTF8,
            ct).ConfigureAwait(false);

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

    private async Task<string> ReadManifestTextAsync(string location, CancellationToken ct)
    {
        if (Directory.Exists(location))
            location = Path.Combine(location, ManifestFileName);

        if (File.Exists(location))
            return await File.ReadAllTextAsync(location, ct).ConfigureAwait(false);

        if (!Uri.TryCreate(location, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException($"不认识的更新地址：{location}");

        return await http.GetStringAsync(uri, ct).ConfigureAwait(false);
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
        if (file.Size > 0 && info.Length != file.Size)
            return false;

        if (string.IsNullOrWhiteSpace(file.Sha256))
            return true;

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
