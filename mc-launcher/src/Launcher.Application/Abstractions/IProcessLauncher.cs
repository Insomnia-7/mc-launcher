using Launcher.Domain.Launch;

namespace Launcher.Application.Abstractions;

/// <summary>起游戏进程。平台差异（argfile、重定向、编码）全部留在实现里。</summary>
public interface IProcessLauncher
{
    /// <param name="environment">额外的环境变量（如把 TEMP 指到可写目录），null 表示继承当前环境。</param>
    IGameProcess Start(LaunchPlan plan, string logFilePath, IReadOnlyDictionary<string, string>? environment = null);
}

public interface IGameProcess : IDisposable
{
    int Id { get; }
    bool HasExited { get; }
    Task<int> WaitForExitAsync(CancellationToken ct = default);
    void Kill();
}
