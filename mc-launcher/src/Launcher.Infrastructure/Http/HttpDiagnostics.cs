namespace Launcher.Infrastructure.Http;

/// <summary>
/// HTTP 层的诊断出口。默认关闭；<c>MCLAUNCHER_HTTP_DIAG=1</c> 或调用 <see cref="Configure"/>
/// 之后，每条请求「先走哪条通道、失败在哪一层、什么时候换道重试」都打一行。
/// <para>
/// 界面进程没有可见控制台，所以这里只负责「查问题时能开」，真正的用户可见信息是异常消息里的
/// URL 与两条通道的失败原因——那些不依赖这个开关。
/// </para>
/// </summary>
public static class HttpDiagnostics
{
    private static Action<string>? _sink;

    static HttpDiagnostics()
    {
        if (Environment.GetEnvironmentVariable("MCLAUNCHER_HTTP_DIAG") is { Length: > 0 } flag && flag != "0")
            _sink = message => Console.Error.WriteLine("[http] " + message);
    }

    public static bool Enabled => Volatile.Read(ref _sink) is not null;

    /// <summary>换一个收集器（CLI 的 --verbose、自检代码都用得上）。传 null 关闭。</summary>
    public static void Configure(Action<string>? sink) => Volatile.Write(ref _sink, sink);

    public static void Log(string message)
    {
        if (Volatile.Read(ref _sink) is not { } sink) return;

        try
        {
            sink(message);
        }
        catch
        {
            // 诊断通道自己出错绝不能影响请求本身
        }
    }
}
