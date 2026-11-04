using System.Diagnostics;
using System.IO;
using System.Text;
using Launcher.Infrastructure.Java;
using Launcher.Application.Abstractions;
using Launcher.Domain.Launch;

namespace Launcher.Infrastructure.Processes;

/// <summary>
/// 起游戏进程：命令行短就直接传参数（宽字符，Unicode 安全），太长才退到 @argfile，
/// 并把 stdout/stderr 落日志。
///
/// 两条路的分工是有原因的：
/// <list type="bullet">
/// <item>命令行直传走 <see cref="ProcessStartInfo.ArgumentList"/>，最终经 CreateProcessW
/// 传宽字符，中文路径不会失真；代价是整条命令行上限 32767 字符（原版 26.3 的 classpath
/// 约 7.6k 字符，够用；堆了几百个模组才会超）。</item>
/// <item>@argfile 在 Windows 上必须按系统 ANSI 代码页写（见 <see cref="ArgFileEncoding"/>）：
/// 写成 UTF-8 时 java 读到的中文 classpath 条目是乱码，客户端 jar 那条直接失效，报
/// 「启动失败：缺少类：net.minecraft.client.main.Main」。</item>
/// </list>
/// argfile 路径优先放日志目录旁边，出问题能直接看到当时到底传了什么；
/// 该目录不可写（Program Files 安装、只读磁盘、受限令牌）时依次退到 TEMP、当前目录，
/// 不让"写不了日志"这种小事把整个启动堵死。
/// </summary>
public sealed class WinProcessLauncher(IJavaLocator? javaLocator = null) : IProcessLauncher
{
    /// <summary>
    /// 命令行直传的安全上限。Windows 的硬上限是 32767（含 exe 路径），这里留一点余量给
    /// 引号与空格；超过才落 @argfile。
    /// </summary>
    public const int DirectCommandLineLimit = 30000;

    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IJavaLocator _javaLocator = javaLocator ?? new JavaLocator();

    public IGameProcess Start(LaunchPlan plan, string logFilePath, IReadOnlyDictionary<string, string>? environment = null)
    {
        // 实例目录还没建过时直接 Process.Start 会报 Win32 267「目录名称无效」——先兜住
        if (!Directory.Exists(plan.WorkingDirectory)) Directory.CreateDirectory(plan.WorkingDirectory);

        var arguments = DirectArguments(plan);
        var useArgFile = SupportsArgFile(plan.JavaPath) && NeedsArgFile(plan.JavaPath, arguments);

        var (argFilePath, effectiveLogPath) = PrepareWritableFiles(plan, logFilePath, useArgFile);

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

        if (argFilePath is not null)
        {
            startInfo.ArgumentList.Add("@" + argFilePath);
        }
        else
        {
            // Java 8 及更老不认 @argfile（JDK 9 才引入），只能一条条塞。
            // 1.12.2 / 1.7.10 这类必须用 Java 8 的版本以前会直接报
            // 「找不到或无法加载主类 @...args.txt」，就是这里。
            // 命令行还在 32767 以内时也走这条：宽字符直传，中文路径天然不会坏。
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment) startInfo.Environment[key] = value;
        }

        var process = System.Diagnostics.Process.Start(startInfo)
                      ?? throw new InvalidOperationException("无法启动 Java 进程：Process.Start 返回 null");

        return new GameProcess(process, effectiveLogPath);
    }

    /// <summary>最终要交给 java 的参数序列：JVM 参数 → 主类 → 游戏参数。</summary>
    public static IReadOnlyList<string> DirectArguments(LaunchPlan plan)
    {
        var arguments = new List<string>(plan.JvmArguments.Count + plan.GameArguments.Count + 1);
        arguments.AddRange(plan.JvmArguments);
        arguments.Add(plan.MainClass);
        arguments.AddRange(plan.GameArguments);
        return arguments;
    }

    /// <summary>估算 CreateProcess 看到的那条命令行长度（每个参数按「空格 + 可能的引号」计）。</summary>
    public static int EstimateCommandLineLength(string javaPath, IReadOnlyList<string> arguments)
    {
        var length = javaPath.Length + 2;
        foreach (var argument in arguments) length += argument.Length + 3;
        return length;
    }

    /// <summary>命令行超限时才需要 @argfile。</summary>
    public static bool NeedsArgFile(string javaPath, IReadOnlyList<string> arguments) =>
        EstimateCommandLineLength(javaPath, arguments) > DirectCommandLineLimit;

    /// <summary>
    /// argfile 的字节内容。Windows 上按 ANSI 写（JVM 就是这么读的：UTF-8 会把中文 classpath
    /// 解成乱码）；系统代码页表示不了里面的字符时才退回 UTF-8，至少不会比原来更差。
    /// 非 Windows 的 JVM 本来就按 UTF-8 读 argfile，保持 UTF-8。
    /// </summary>
    public static byte[] ArgFileBytes(LaunchPlan plan)
    {
        var content = plan.ToArgFileContent();
        if (OperatingSystem.IsWindows() && ArgFileEncoding.TryEncodeAnsi(content) is { } ansi) return ansi;
        return Utf8WithoutBom.GetBytes(content);
    }

    /// <summary>
    /// Java 主版本 ≥ 9 才有 @argfile。探测一次就缓存：<c>java -version</c> 的 stderr 里
    /// 形如 <c>version "1.8.0_421"</c> 或 <c>version "21.0.1"</c>；探不出来时按「支持」处理，
    /// 保持原来行为（真起不来还能从日志看到命令行）。
    /// </summary>
    private bool SupportsArgFile(string javaPath) =>
        (_javaLocator.ProbeAsync(javaPath).GetAwaiter().GetResult()?.MajorVersion ?? 9) >= 9;

    /// <summary>
    /// 依次尝试「给定日志目录 → TEMP → 当前目录 → 工作目录」，返回第一个能写的日志路径；
    /// 需要 argfile 时顺手把 .args.txt 写在同一处。
    /// </summary>
    private static (string? ArgFilePath, string LogFilePath) PrepareWritableFiles(
        LaunchPlan plan,
        string logFilePath,
        bool writeArgFile)
    {
        var fileName = Path.GetFileName(logFilePath);

        var candidates = new List<string> { logFilePath };
        try { candidates.Add(Path.Combine(Path.GetTempPath(), fileName)); } catch (Exception) { /* 取不到 TEMP 就算了 */ }
        try { candidates.Add(Path.Combine(Environment.CurrentDirectory, fileName)); } catch (Exception) { /* 同上 */ }
        candidates.Add(Path.Combine(plan.WorkingDirectory, fileName));

        var argFileBytes = writeArgFile ? ArgFileBytes(plan) : null;
        var failures = new List<string>();

        foreach (var candidate in candidates)
        {
            try
            {
                var directory = Path.GetDirectoryName(candidate);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                // 先真建一次日志文件证明这个目录可写（GameProcess 会以 FileMode.Create 重开它）
                using (new FileStream(candidate, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { }

                string? argPath = null;
                if (argFileBytes is not null)
                {
                    argPath = Path.ChangeExtension(candidate, ".args.txt");
                    File.WriteAllBytes(argPath, argFileBytes);
                }

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
                _log = new StreamWriter(logStream, Utf8WithoutBom) { AutoFlush = true };
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
