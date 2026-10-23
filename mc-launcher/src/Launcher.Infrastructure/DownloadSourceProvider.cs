using Launcher.Application.Abstractions;
using Launcher.Application.Settings;

namespace Launcher.Infrastructure;

/// <summary>
/// 长期存活的下载源：每次改写前按 settings.json 的写入时间判断有没有变，
/// 变了就重读。这样"设置界面改完立刻生效"不需要重建整个 DI 图，
/// 而没改设置时只是一个文件时间戳 stat，成本可以忽略。
/// </summary>
public sealed class DownloadSourceProvider : IUrlRewriter
{
    private readonly object _gate = new();
    private readonly string _settingsPath;

    private bool _loaded;
    private DateTime _stamp;
    private DownloadSource _current = DownloadSource.Official;

    /// <param name="dataDirectory">数据目录；null 表示按 <c>MCLAUNCHER_HOME</c> / <c>%APPDATA%</c> 解析。</param>
    public DownloadSourceProvider(string? dataDirectory = null)
        : this(LauncherDataPaths.ResolveSettingsFile(dataDirectory), ownsPath: true)
    {
    }

    private DownloadSourceProvider(string settingsPath, bool ownsPath)
    {
        _ = ownsPath;
        _settingsPath = settingsPath;
    }

    /// <summary>直接盯住某个 settings.json 文件（测试 / 非标准布局用）。</summary>
    public static DownloadSourceProvider ForSettingsFile(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        return new DownloadSourceProvider(settingsPath, ownsPath: true);
    }

    public string SettingsPath => _settingsPath;

    /// <summary>当前生效的源（会先做一次变更检查）。</summary>
    public DownloadSource Current
    {
        get
        {
            Refresh();
            lock (_gate) return _current;
        }
    }

    public string Rewrite(string url)
    {
        var source = Current;
        return source == DownloadSource.Bmclapi ? SourceRewriter.Apply(url, useMirror: true) : url;
    }

    /// <summary>丢弃缓存，下次访问重新读文件（写设置后想立刻稳住时用）。</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _loaded = false;
            _stamp = default;
        }
    }

    private void Refresh()
    {
        DateTime stamp;
        try
        {
            stamp = File.Exists(_settingsPath) ? File.GetLastWriteTimeUtc(_settingsPath) : default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;   // 读不到时间戳就沿用上一次的值
        }

        lock (_gate)
        {
            if (_loaded && stamp == _stamp) return;

            _current = DownloadSourceStore.ReadFile(_settingsPath);
            _stamp = stamp;
            _loaded = true;
        }
    }
}
