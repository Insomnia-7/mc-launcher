using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Launcher.Application.Settings;
using Launcher.Infrastructure.Http;

internal static class NetworkSettingsChecks
{
    public static async Task RunAsync(Action<string, bool, string?> check)
    {
        void Check(string title, bool value) => check(title, value, null);
        var root = Path.Combine(Path.GetTempPath(), "mcl-network-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = LauncherDataPaths.ResolveSettingsFile(root);
        try
        {
            Check("未配置代理时保持自动模式", ProxySettingsStore.Read(root) == new ProxySettings());
            File.WriteAllText(path, "{\"downloadSource\":\"bmclapi\",\"extra\":{\"keep\":true}}");
            ProxySettingsStore.Write(new(ProxyMode.Custom, "  http://127.0.0.1:7890/  "), root);
            Check("代理配置规范化并可重新读取", ProxySettingsStore.Read(root) == new ProxySettings(ProxyMode.Custom, "http://127.0.0.1:7890"));
            var json = JsonNode.Parse(File.ReadAllText(path))!;
            Check("保存代理保留下载源和其它配置", json["downloadSource"]!.GetValue<string>() == "bmclapi" && json["extra"]!["keep"]!.GetValue<bool>());
            var original = File.ReadAllText(path);
            foreach (var address in new[] { "", "127.0.0.1:7890", "ftp://localhost:1234", "http://user:pass@localhost:1234", "http://localhost:1234/path", "http://localhost:1234?key=x", "http://localhost:1234#x", "socks5://localhost", "http://localhost:0", "http://localhost:99999", "http://local host:1234" })
            {
                var rejected = false;
                try { ProxySettingsStore.Write(new(ProxyMode.Custom, address), root); }
                catch (ArgumentException) { rejected = true; }
                Check("拒绝无效代理且不改变原设置：" + (address.Contains("user:") ? "带凭据地址" : address), rejected && File.ReadAllText(path) == original);
            }
            foreach (var address in new[] { "socks5://127.0.0.1:1080", "https://proxy.example:443", "http://[::1]:7890" })
                Check("支持代理格式 " + address, new ProxySettings(ProxyMode.Custom, address).Validate().Mode == ProxyMode.Custom);
            ProxySettingsStore.Write(new(ProxyMode.Direct, "unused"), root);
            Check("切换直连清除不用的代理地址", ProxySettingsStore.Read(root) == new ProxySettings(ProxyMode.Direct));
            Check("保存不遗留临时配置", Directory.GetFiles(root, "*.tmp").Length == 0);
            File.WriteAllText(path, "{\"proxy\":{\"mode\":\"unknown\"}}");
            var invalid = false;
            try { _ = ProxySettingsStore.Read(root); } catch (InvalidDataException) { invalid = true; }
            Check("错误代理配置不会静默解释为自动直连", invalid);
            ProxySettingsStore.Write(new(), root);
            Check("重新保存可修复无效代理字段", ProxySettingsStore.Read(root) == new ProxySettings());
            File.WriteAllText(path, "broken json");
            var failed = false;
            try { ProxySettingsStore.Write(new(), root); } catch (System.Text.Json.JsonException) { failed = true; }
            Check("损坏设置文件保存失败且不抹掉原内容", failed && File.ReadAllText(path) == "broken json");

            await using var origin = new LocalEndpoint("origin");
            await using var proxy = new LocalEndpoint("proxy");
            var previous = HttpClient.DefaultProxy;
            HttpClient.DefaultProxy = new WebProxy(proxy.Address);
            try
            {
                using var direct = HttpClientFactory.Create(timeout: TimeSpan.FromSeconds(3), proxySettings: new(ProxyMode.Direct));
                Check("直连模式忽略系统代理", await direct.GetStringAsync(origin.Address) == "origin" && proxy.Requests == 0);
                using var custom = HttpClientFactory.Create(timeout: TimeSpan.FromSeconds(3), proxySettings: new(ProxyMode.Custom, proxy.Address));
                Check("自定义代理接收实际请求", await custom.GetStringAsync(origin.Address) == "proxy" && origin.Requests == 1);
                Check("代理请求使用完整目标地址", proxy.LastRequest.StartsWith("GET " + origin.Address + "/ HTTP/"));
                proxy.StatusCode = 503;
                using var rejected = await custom.GetAsync(origin.Address);
                Check("自定义代理服务失败不回退直连", rejected.StatusCode == HttpStatusCode.ServiceUnavailable && origin.Requests == 1);
                using var post = await custom.PostAsync(origin.Address, new StringContent("fixture"));
                Check("认证类 POST 也使用指定代理", proxy.LastRequest.StartsWith("POST ") && origin.Requests == 1);
                var unused = new TcpListener(IPAddress.Loopback, 0);
                unused.Start(); var port = ((IPEndPoint)unused.LocalEndpoint).Port; unused.Stop();
                using var dead = HttpClientFactory.Create(timeout: TimeSpan.FromSeconds(3), proxySettings: new(ProxyMode.Custom, $"http://127.0.0.1:{port}"));
                var unreachable = false;
                try { await dead.GetAsync(origin.Address); } catch (HttpRequestException) { unreachable = true; }
                Check("自定义代理不可达时失败且不访问源站", unreachable && origin.Requests == 1);
            }
            finally { HttpClient.DefaultProxy = previous; }

            var initialized = 0;
            var tracking = new TrackingHandler();
            using (var client = new HttpClient(new DeferredHttpHandler(() => { Interlocked.Increment(ref initialized); return tracking; })))
            {
                Check("创建客户端不探测代理或建立通道", initialized == 0);
                var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.GetAsync("https://fixture.invalid/")));
                foreach (var response in responses) response.Dispose();
                Check("并发首批请求只初始化一次通道", initialized == 1 && tracking.Requests == 8);
            }
            Check("关闭客户端释放延迟通道", tracking.Disposed);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<HttpMessageHandler>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancellationTracking = new TrackingHandler();
            using (var client = new HttpClient(new DeferredHttpHandler(() => { started.TrySetResult(); return release.Task.GetAwaiter().GetResult(); })))
            {
                using var cts = new CancellationTokenSource();
                var pending = client.GetAsync("https://fixture.invalid/", cts.Token);
                await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
                cts.Cancel();
                var cancelled = false;
                try { await pending; } catch (OperationCanceledException) { cancelled = true; }
                release.SetResult(cancellationTracking);
                using var response = await client.GetAsync("https://fixture.invalid/");
                Check("取消首次请求后后续请求仍可使用通道", cancelled && cancellationTracking.Requests == 1);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class TrackingHandler : HttpMessageHandler
    {
        public int Requests;
        public bool Disposed;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class LocalEndpoint : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        public string Address { get; }
        public int Requests;
        public int StatusCode = 200;
        public string LastRequest = "";
        public LocalEndpoint(string body)
        {
            _listener.Start();
            Address = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _loop = Task.Run(async () =>
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        using var connection = await _listener.AcceptTcpClientAsync(_stop.Token);
                        await using var stream = connection.GetStream();
                        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                        LastRequest = await reader.ReadLineAsync(_stop.Token) ?? "";
                        var length = 0;
                        string? line;
                        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(_stop.Token)))
                            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line[15..]);
                        if (length > 0) await reader.ReadBlockAsync(new char[length], _stop.Token);
                        Interlocked.Increment(ref Requests);
                        var response = Encoding.ASCII.GetBytes($"HTTP/1.1 {StatusCode} Fixture\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}");
                        await stream.WriteAsync(response, _stop.Token);
                    }
                }
                catch (OperationCanceledException) { }
            });
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            await _loop;
            _listener.Stop();
            _stop.Dispose();
        }
    }
}
