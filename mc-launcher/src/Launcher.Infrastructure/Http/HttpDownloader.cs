using System.Collections.Concurrent;
using System.Buffers;
using Launcher.Application.Install;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Launcher.Application.Abstractions;
using Launcher.Domain.Install;

namespace Launcher.Infrastructure.Http;

public sealed record DownloadOptions(
    int MaxConcurrency = 16,
    int MaxRetries = 3,
    TimeSpan? PerFileTimeout = null,
    TimeSpan? StallTimeout = null,
    long MinBytesPerSecond = 10 * 1024)
{
    /// <summary>
    /// 单文件超时。HttpClient.Timeout 只管到响应头，body 卡住会永远挂着 —— 必须自己兜。
    /// </summary>
    public TimeSpan EffectivePerFileTimeout => PerFileTimeout ?? TimeSpan.FromSeconds(120);

    /// <summary>
    /// 停滞窗口：连续这么久平均速率低于 <see cref="MinBytesPerSecond"/> 就放弃并重试。
    /// 国内到 cdn.modrinth.com / forgecdn 经常掉到几十 KB/s，光靠整文件超时会让用户干等两分钟。
    /// </summary>
    public TimeSpan EffectiveStallTimeout => StallTimeout ?? TimeSpan.FromSeconds(60);

    public static readonly DownloadOptions Default = new();
}

/// <summary>下载停滞（速率长时间低于阈值），交给重试逻辑换一次连接。</summary>
public sealed class DownloadStalledException(string message) : IOException(message);

/// <summary>
/// 服务器限流（429）或明确说忙（503）。和停滞不同，这里要按服务器给的 Retry-After 等，
/// 不能 300ms 硬撞——BMCLAPI 就是按出口 IP 的令牌桶限流，硬撞只会让窗口越拉越长。
/// </summary>
public sealed class DownloadThrottledException(string message, TimeSpan retryAfter) : IOException(message)
{
    public TimeSpan RetryAfter { get; } = retryAfter;
}

/// <summary>
/// 并发下载器：限流 + sha1 校验 + .part 断点续传。
/// 设计取舍：校验失败重下一次而不是三次；.part 命名带 url 指纹，避免换版本错用（蓝图 §10 坑 18）。
/// </summary>
public sealed class HttpDownloader(HttpClient http, DownloadOptions? options = null) : IDownloader
{
    private readonly DownloadOptions _options = options ?? DownloadOptions.Default;

    public async Task<DownloadSummary> DownloadAsync(
        IReadOnlyList<DownloadEntry> entries,
        string rootDirectory,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(rootDirectory);

        var totalBytes = entries.Where(e => e.Size > 0).Sum(e => e.Size);
        var totalFiles = entries.Count;
        var counters = new Counters();
        var errors = new ConcurrentBag<(DownloadEntry Entry, Exception Error)>();
        var stopwatch = Stopwatch.StartNew();

        using var gate = new SemaphoreSlim(_options.MaxConcurrency);

        await Parallel.ForEachAsync(
            entries,
            new ParallelOptions { MaxDegreeOfParallelism = _options.MaxConcurrency, CancellationToken = ct },
            async (entry, token) =>
            {
                await gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (Path.IsPathRooted(entry.RelativePath)) throw new InvalidDataException("下载目标必须是相对路径。");
                    var target = LongPath.Normalize(LongPath.Combine(Path.GetFullPath(rootDirectory), entry.RelativePath));

                    if (await IsUpToDateAsync(target, entry, token).ConfigureAwait(false))
                    {
                        Interlocked.Increment(ref counters.Skipped);
                        var afterSkip = Interlocked.Add(ref counters.CompletedBytes, Math.Max(0, entry.Size));
                        var completed = Interlocked.Increment(ref counters.CompletedFiles);
                        progress?.Report(new DownloadProgress(afterSkip, totalBytes, completed, totalFiles, entry.RelativePath));
                        return;
                    }

                    var written = await DownloadWithRetryAsync(entry, target, token).ConfigureAwait(false);

                    Interlocked.Increment(ref counters.Downloaded);
                    Interlocked.Add(ref counters.DownloadedBytes, written);
                    var after = Interlocked.Add(ref counters.CompletedBytes, written);
                    var completedFiles = Interlocked.Increment(ref counters.CompletedFiles);
                    progress?.Report(new DownloadProgress(after, totalBytes, completedFiles, totalFiles, entry.RelativePath));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    errors.Add((entry, ex));
                }
                finally
                {
                    gate.Release();
                }
            }).ConfigureAwait(false);

        stopwatch.Stop();

        if (!errors.IsEmpty)
        {
            throw new AggregateException(
                $"{errors.Count}/{totalFiles} 个文件下载失败",
                errors.OrderBy(e => e.Entry.RelativePath, StringComparer.Ordinal).Select(e => e.Error));
        }

        progress?.Report(new DownloadProgress(counters.CompletedBytes, totalBytes, counters.CompletedFiles, totalFiles, null));
        return new DownloadSummary(totalFiles, counters.Downloaded, counters.Skipped, counters.DownloadedBytes, stopwatch.Elapsed);
    }

    /// <summary>跨线程计数器。必须放在类字段上，async lambda 里不能对捕获的局部变量取 ref。</summary>
    private sealed class Counters
    {
        public long CompletedBytes;
        public long DownloadedBytes;
        public int CompletedFiles;
        public int Downloaded;
        public int Skipped;
    }

    private async Task<bool> IsUpToDateAsync(string path, DownloadEntry entry, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return false;
        if (entry.Size > 0 && info.Length != entry.Size) return false;
        if (string.IsNullOrWhiteSpace(entry.Sha1)) return true;

        return string.Equals(await ComputeSha1Async(path, ct).ConfigureAwait(false), entry.Sha1, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<long> DownloadWithRetryAsync(DownloadEntry entry, string target, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        var partPath = target + ".part";
        Exception? lastError = null;
        var url = entry.Url;
        var switchedToFallback = false;

        for (var attempt = 1; attempt <= _options.MaxRetries; attempt++)
        {
            try
            {
                long written = 0;

                if (File.Exists(partPath))
                {
                    var existing = new FileInfo(partPath).Length;
                    if (entry.Size > 0 && existing > entry.Size) File.Delete(partPath);   // .part 比目标还大，说明脏了
                    else if (entry.Size > 0 && existing == entry.Size)
                    {
                        if (await IsUpToDateAsync(partPath, entry, ct).ConfigureAwait(false))
                        {
                            File.Move(partPath, target, overwrite: true);
                            return existing;
                        }
                        File.Delete(partPath);
                    }
                    else written = existing;
                }

                // 上一次尝试失败且这条有备用地址（镜像改写前记下的官方 URL）→ 换回官方再试。
                // 只切一次，之后固定用官方：镜像 404 / 超时 / 限流都算，官方源不该被镜像连累。
                if (lastError is not null && !switchedToFallback && entry.FallbackUrl is { Length: > 0 })
                {
                    url = entry.FallbackUrl;
                    switchedToFallback = true;
                }

                written = await FetchIntoAsync(url, partPath, written, ct).ConfigureAwait(false);

                var actualSize = new FileInfo(partPath).Length;
                if (entry.Size > 0 && actualSize != entry.Size)
                    throw new IOException($"大小不符：期望 {entry.Size}，实际 {actualSize}");

                if (!string.IsNullOrWhiteSpace(entry.Sha1))
                {
                    var sha1 = await ComputeSha1Async(partPath, ct).ConfigureAwait(false);
                    if (!string.Equals(sha1, entry.Sha1, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(partPath);
                        throw new InvalidDataException($"sha1 不符：期望 {entry.Sha1}，实际 {sha1}");
                    }
                }

                File.Move(partPath, target, overwrite: true);
                return written;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 单文件超时（FetchInto 内部的 linked CTS 触发），不是用户取消 → 允许重试
                lastError = new TimeoutException(
                    $"单文件超过 {_options.EffectivePerFileTimeout.TotalSeconds:F0}s 未完成：{entry.Url}");
                if (attempt < _options.MaxRetries)
                    await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (DownloadThrottledException ex)
            {
                lastError = ex;
                if (attempt < _options.MaxRetries)
                {
                    // 限流要按服务器窗口等，别用 300ms 硬撞（BMCLAPI 按出口 IP 计速率，撞一次窗口更久）
                    var wait = Clamp(ex.RetryAfter, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(10));
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt < _options.MaxRetries)
                    await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt), ct).ConfigureAwait(false);
            }
        }

        var hint = lastError switch
        {
            DownloadStalledException => "（源太慢，可以换镜像 / 稍后重试 / 检查代理）",
            DownloadThrottledException => "（源在限流，稍后重试或换源）",
            _ => string.Empty,
        };

        throw new IOException($"下载失败（{_options.MaxRetries} 次尝试）{hint}：{url}", lastError);
    }

    private async Task<long> FetchIntoAsync(string url, string partPath, long alreadyHave, CancellationToken ct)
    {
        // 整文件级别的超时：连接建立 + 传输全算进去，卡住就取消并交给重试
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_options.EffectivePerFileTimeout);
        var token = timeoutCts.Token;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (alreadyHave > 0)
            request.Headers.Range = new RangeHeaderValue(alreadyHave, null);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            File.Delete(partPath); // 远端已拒绝此断点，下次尝试从头下载。
        if (response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange?.From != alreadyHave)
        {
            File.Delete(partPath);
            throw new InvalidDataException("续传响应的 Content-Range 与本地断点不一致。");
        }

        var append = alreadyHave > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!append) alreadyHave = 0;

        if (IsThrottled(response.StatusCode))
            throw new DownloadThrottledException(
                $"源在限流 {(int)response.StatusCode}：{url}",
                RetryAfterOf(response));

        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var destination = new FileStream(
            partPath,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);

        await CopyWithStallDetectionAsync(source, destination, token).ConfigureAwait(false);
        await destination.FlushAsync(token).ConfigureAwait(false);

        return destination.Length;
    }

    private static bool IsThrottled(HttpStatusCode status) =>
        status is (HttpStatusCode)429 or HttpStatusCode.ServiceUnavailable;

    /// <summary>
    /// 优先相信服务器给的 Retry-After（秒数或 HTTP-date），没有就保守等 1.5 秒。
    /// 不做更长的等待：用户就在旁边看着进度条，等 30 秒不如换个源。
    /// </summary>
    private static TimeSpan RetryAfterOf(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta && delta > TimeSpan.Zero) return delta;

        if (header?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) return wait;
        }

        return TimeSpan.FromSeconds(1.5);
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) =>
        value < min ? min : value > max ? max : value;

    /// <summary>每次采样的状态。看门狗和读循环跨线程共享，只能走字段。</summary>
    private sealed class TransferState
    {
        public long Bytes;
        public volatile bool Stalled;
    }

    /// <summary>
    /// 边传边看：每 2 秒采样一次，滑窗里平均速率低于阈值就取消整条连接。
    /// 取消会打断正在阻塞的 ReadAsync，所以停滞的流不会把线程吊死。
    /// </summary>
    private async Task CopyWithStallDetectionAsync(Stream source, Stream destination, CancellationToken ct)
    {
        var state = new TransferState();
        using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = stallCts.Token;

        var watchdog = WatchAsync();
        async Task WatchAsync()
        {
            var sampleInterval = TimeSpan.FromSeconds(2);
            var window = _options.EffectiveStallTimeout;
            var samples = new Queue<(double Seconds, long Bytes)>();
            var windowSeconds = 0d;
            var windowBytes = 0L;
            var lastBytes = 0L;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(sampleInterval, token).ConfigureAwait(false);

                    var current = Volatile.Read(ref state.Bytes);
                    var delta = current - lastBytes;
                    lastBytes = current;

                    // 首字节还没到：交给整文件超时兜，不在这里判停滞（首字节延迟高很常见）
                    if (current == 0) continue;

                    samples.Enqueue((sampleInterval.TotalSeconds, delta));
                    windowSeconds += sampleInterval.TotalSeconds;
                    windowBytes += delta;

                    while (windowSeconds > window.TotalSeconds && samples.Count > 1)
                    {
                        var (seconds, bytes) = samples.Dequeue();
                        windowSeconds -= seconds;
                        windowBytes -= bytes;
                    }

                    if (windowSeconds < window.TotalSeconds) continue;

                    var rate = windowBytes / windowSeconds;
                    if (rate >= _options.MinBytesPerSecond) continue;

                    state.Stalled = true;
                    await stallCts.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                // 正常结束
            }
        }

        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, 81920), token).ConfigureAwait(false);
                if (read == 0) break;

                Volatile.Write(ref state.Bytes, Volatile.Read(ref state.Bytes) + read);
                await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (state.Stalled)
        {
            throw new DownloadStalledException(
                $"下载停滞：{_options.EffectiveStallTimeout.TotalSeconds:F0} 秒内平均低于 {_options.MinBytesPerSecond / 1024} KB/s");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            await stallCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await watchdog.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 忽略
            }
        }
    }

    private static async Task<string> ComputeSha1Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hash = await SHA1.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }
}
