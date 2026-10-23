namespace Launcher.Application.Operations;

/// <summary>同一进程内，同一游戏根目录只执行一个写任务；不同目录与只读操作互不阻塞。</summary>
public sealed class GameOperationCoordinator
{
    public static GameOperationCoordinator Shared { get; } = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _active = new(StringComparer.OrdinalIgnoreCase);

    public IDisposable? TryEnter(string gameRoot, string operation)
    {
        var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameRoot));
        lock (_gate)
        {
            if (_active.ContainsKey(key)) return null;
            _active.Add(key, operation);
            return new Lease(() => { lock (_gate) _active.Remove(key); });
        }
    }

    public string BusyMessage(string gameRoot)
    {
        var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameRoot));
        lock (_gate) return _active.TryGetValue(key, out var operation) ? $"正在{operation}，请等待完成或取消该任务。" : "任务已结束，请重试。";
    }

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
