namespace Launcher.Application.Abstractions;

/// <summary>下载进度快照。Total 为 0 表示总量未知。</summary>
public sealed record DownloadProgress(
    long CompletedBytes,
    long TotalBytes,
    int CompletedFiles,
    int TotalFiles,
    string? CurrentFile)
{
    public double Percent => TotalBytes <= 0 ? 0 : Math.Clamp(CompletedBytes * 100.0 / TotalBytes, 0, 100);
}

public sealed record DownloadSummary(
    int TotalFiles,
    int DownloadedFiles,
    int SkippedFiles,
    long DownloadedBytes,
    TimeSpan Elapsed)
{
    public double BytesPerSecond => Elapsed.TotalSeconds <= 0 ? 0 : DownloadedBytes / Elapsed.TotalSeconds;
}

public interface IDownloader
{
    /// <summary>
    /// 把清单并发下载到 rootDirectory（相对路径已按平台分隔符拼好）。
    /// 已存在且 sha1 匹配的文件直接跳过；失败项聚合成 AggregateException 抛出，不中断其它文件。
    /// </summary>
    Task<DownloadSummary> DownloadAsync(
        IReadOnlyList<Launcher.Domain.Install.DownloadEntry> entries,
        string rootDirectory,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default);
}
