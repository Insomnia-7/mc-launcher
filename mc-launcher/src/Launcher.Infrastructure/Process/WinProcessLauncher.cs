using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Launcher.Application.Abstractions;
using Launcher.Domain.Launch;

namespace Launcher.Infrastructure.Processes;

/// <summary>
/// Windows 起进程：写 @argfile → java @file → stdout/stderr 落日志。
/// argfile 路径优先放日志目录旁边，出问题能直接看到当时到底传了什么；
/// 该目录不可写（Program Files 安装、只读磁盘、受限令牌）时依次退到 TEMP、当前目录，
/// 不让"写不了日志"这种小事把整个启动堵死。
/// </summary>
public sealed class WinProcessLauncher : IProcessLauncher
{
    public IGameProcess Start(LaunchPlan plan, string logFilePath, IReadOnlyDictionary<string, string>? environment = null)
    {
        // 实例目录还没建过时直接 Process.Start 会报 Win32 267「目录名称无效」——先兜住
        if (!Directory.Exists(plan.WorkingDirectory)) Directory.CreateDirectory(plan.WorkingDirectory);

        var (argFilePath, effectiveLogPath) = PrepareWritableFiles(plan, logFilePath);

        var startInfo = new ProcessStartInfo(plan.JavaPath)
        {
            WorkingDirectory = plan.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (SupportsArgFile(plan.JavaPath))
        {
            startInfo.ArgumentList.Add("@" + argFilePath);
        }
        else
        {
            // Java 8 及更老不认 @argfile（JDK 9 才引入），只能一条条塞。
            // 1.12.2 / 1.7.10 这类必须用 Java 8 的版本以前会直接报
            // 「找不到或无法加载主类 @...args.txt」，就是这里。
            foreach (var argument in plan.JvmArguments) startInfo.ArgumentList.Add(argument);
            startInfo.ArgumentList.Add(plan.MainClass);
            foreach (var argument in plan.GameArguments) startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment) startInfo.Environment[key] = value;
        }

        var process = System.Diagnostics.Process.Start(startInfo)
                      ?? throw new InvalidOperationException("无法启动 Java 进程：Process.Start 返回 null");

        return new GameProcess(process, effectiveLogPath);
    }

    /// <summary>
    /// Java 主版本 ≥ 9 才有 @argfile。探测一次就缓存：<c>java -version</c> 的 stderr 里
    /// 形如 <c>version "1.8.0_421"</c> 或 <c>version "21.0.1"</c>；探不出来时按「支持」处理，
    /// 保持原来行为（真起不来还能从日志看到命令行）。
    /// </summary>
    private static bool SupportsArgFile(string javaPath) =>
        ArgFileSupport.GetOrAdd(javaPath, DetectArgFileSupport);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> ArgFileSupport =
        new(StringComparer.OrdinalIgnoreCase);

    private static bool DetectArgFileSupport(string javaPath)
    {
        try
        {
            var probe = new ProcessStartInfo(javaPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            probe.ArgumentList.Add("-version");

            using var process = System.Diagnostics.Process.Start(probe);
            if (process is null) return true;

            var output = process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            var match = Regex.Match(output, "version \"(?:1\\.)?(\\d+)");
            if (!match.Success) return true;

            return int.Parse(match.Groups[1].Value) >= 9;
        }
        catch (Exception)
        {
            // 探测失败不该影响启动：按支持处理，真不认 @ 的话错误也会写进日志
            return true;
        }
    }

    /// <summary>
    /// 依次尝试「给定日志目录 → TEMP → 当前目录 → 工作目录」，返回第一个能写的 argfile 与日志路径。
    /// </summary>
    private static (string ArgFilePath, string LogFilePath) PrepareWritableFiles(LaunchPlan plan, string logFilePath)
    {
        var fileName = Path.GetFileName(logFilePath);

        var candidates = new List<string> { logFilePath };
        try { candidates.Add(Path.Combine(Path.GetTempPath(), fileName)); } catch (Exception) { /* 取不到 TEMP 就算了 */ }
        try { candidates.Add(Path.Combine(Environment.CurrentDirectory, fileName)); } catch (Exception) { /* 同上 */ }
        candidates.Add(Path.Combine(plan.WorkingDirectory, fileName));

        var content = plan.ToArgFileContent();
        var failures = new List<string>();

        foreach (var candidate in candidates)
        {
            try
            {
                var directory = Path.GetDirectoryName(candidate);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                var argPath = Path.ChangeExtension(candidate, ".args.txt");
                File.WriteAllText(argPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                return (argPath, candidate);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                failures.Add($"{candidate}: {ex.Message}");
            }
        }

        throw new IOException(
            "所有候选位置都写不了启动参数文件：" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    private sealed class GameProcess : IGameProcess
    {
        private readonly System.Diagnostics.Process _process;
        private readonly StreamWriter? _log;
        private readonly object _writeLock = new();

        public GameProcess(System.Diagnostics.Process process, string logFilePath)
        {
            _process = process;

            // 日志写不了不是致命错误：照样把游戏跑起来，只是没有落盘日志
            try
            {
                var logStream = new FileStream(logFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                _log = new StreamWriter(logStream, new UTF8Encoding(false)) { AutoFlush = true };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log = null;
            }

            process.OutputDataReceived += (_, e) => Write(e.Data);
            process.ErrorDataReceived += (_, e) => Write(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        public int Id => _process.Id;
        public bool HasExited => _process.HasExited;

        private void Write(string? line)
        {
            if (line is null || _log is null) return;
            lock (_writeLock) _log.WriteLine(line);
        }

        public async Task<int> WaitForExitAsync(CancellationToken ct = default)
        {
            await _process.WaitForExitAsync(ct).ConfigureAwait(false);
            return _process.ExitCode;
        }

        public void Kill()
        {
            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // 已经退了
            }
        }

        public void Dispose()
        {
            try { _process.WaitForExit(2000); } catch (Exception) { /* 忽略 */ }
            lock (_writeLock) _log?.Dispose();
            _process.Dispose();
        }
    }
}
