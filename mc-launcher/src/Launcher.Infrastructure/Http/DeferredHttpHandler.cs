namespace Launcher.Infrastructure.Http;

/// <summary>第一次请求时才在线程池创建连接通道，不在窗口构造期间探测代理。</summary>
public sealed class DeferredHttpHandler(Func<HttpMessageHandler> factory) : HttpMessageHandler
{
    private readonly object _gate = new();
    private Task<HttpMessageInvoker>? _initialization;
    private bool _disposed;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Task<HttpMessageInvoker> initialization;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            initialization = _initialization ??= Task.Run(() => new HttpMessageInvoker(factory(), disposeHandler: true));
        }
        var invoker = await initialization.WaitAsync(ct).ConfigureAwait(false);
        return await invoker.SendAsync(request, ct).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_gate)
            {
                if (!_disposed)
                {
                    _disposed = true;
                    if (_initialization is { } pending)
                        _ = pending.ContinueWith(task =>
                        {
                            if (task.IsCompletedSuccessfully) task.Result.Dispose();
                            else _ = task.Exception;
                        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }
        }
        base.Dispose(disposing);
    }
}
