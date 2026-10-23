// 启动器冷启动基准：直启 MCLauncher.exe，量「主窗口可交互」耗时与内存，输出表格 + JSON，
// 并支持与旧 JSON 基线对比（美化前后防回归）。
//
// 用法：
//   launcher-bench --exe <MCLauncher.exe> [--runs 5] [--out bench.json] [--settle-ms 400]
//                  [--timeout-ms 20000] [--baseline 旧.json]
//
// 口径：Stopwatch 起 → 每 5 ms 轮询 Process.MainWindowHandle != 0 且 Responding → 记 handleMs
//       → 立刻读 WorkingSet64 / PrivateMemorySize64 / PeakWorkingSet64 → Kill(true) → 等 settle-ms。
// 说明：启动器有单实例互斥锁，所以每轮开始前会先关掉同名的残留实例（只会动 --exe 对应的进程名）。

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace Launcher.Bench;

internal static class Program
{
    private const string Schema = "launcher-bench/1";

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        BenchOptions options;
        try
        {
            options = BenchOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine("参数错误：" + ex.Message);
            Console.Error.WriteLine();
            PrintUsage();
            return 2;
        }

        if (options.ShowHelp)
        {
            PrintUsage();
            return 0;
        }

        if (!File.Exists(options.Exe))
        {
            Console.Error.WriteLine($"找不到可执行文件：{options.Exe}");
            return 2;
        }

        var target = TargetInfo.Describe(options.Exe);
        var machine = MachineInfo.Capture();

        Console.WriteLine($"目标：{target.Exe}");
        Console.WriteLine($"      版本 {target.FileVersion ?? "?"} · {target.SizeBytes / 1024.0 / 1024.0:F1} MB · 构建于 {target.LastWriteUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"机器：{machine.CpuName} · {machine.TotalRamMb} MB · {machine.ProcessorCount} 逻辑核 · {machine.OsDescription}");
        Console.WriteLine($"运行时：{machine.FrameworkDescription} · 采样 {options.Runs} 轮，间隔 {options.SettleMs} ms，超时 {options.TimeoutMs} ms");
        Console.WriteLine();

        var runs = new List<RunResult>(options.Runs);
        var failures = 0;

        for (var i = 1; i <= options.Runs; i++)
        {
            RunResult result;
            try
            {
                result = RunOnce(options, i);
                Console.WriteLine(FormatRow(result));
            }
            catch (Win32Exception ex)
            {
                failures++;
                result = RunResult.Failed(i, $"Win32 {ex.NativeErrorCode}: {ex.Message}");
                Console.WriteLine(FormatRow(result));
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
            {
                failures++;
                result = RunResult.Failed(i, ex.Message);
                Console.WriteLine(FormatRow(result));
            }

            runs.Add(result);

            if (i < options.Runs) Thread.Sleep(options.SettleMs);
        }

        var summary = BenchSummary.From(runs);
        Console.WriteLine();
        PrintSummary(summary, runs.Count);

        var document = new BenchDocument(
            Schema,
            DateTimeOffset.Now,
            machine,
            target,
            new BenchOptionsInfo(options.Runs, options.SettleMs, options.TimeoutMs),
            runs,
            summary);

        var outPath = Path.GetFullPath(options.OutPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        var json = JsonSerializer.Serialize(document, JsonOptions);
        File.WriteAllText(outPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine();
        Console.WriteLine($"JSON：{outPath}");

        if (options.BaselinePath is not null) CompareWithBaseline(options.BaselinePath, document);

        if (failures > 0)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"{failures} 轮失败（共 {runs.Count} 轮）");
            return 1;
        }

        return 0;
    }

    // ---------------------------------------------------------------- 单轮

    private static RunResult RunOnce(BenchOptions options, int index)
    {
        KillLeftovers(options.Exe);

        var startInfo = new ProcessStartInfo(options.Exe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(options.Exe)!,
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Process.Start 返回 null（进程没起来）");

        var stopwatch = Stopwatch.StartNew();
        long? handleMs = null;

        while (stopwatch.ElapsedMilliseconds < options.TimeoutMs)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"进程提前退出（exit={process.ExitCode}）——单实例互斥锁或启动即崩，检查启动器日志");
            }

            process.Refresh();
            if (process.MainWindowHandle != 0 && process.Responding)
            {
                handleMs = stopwatch.ElapsedMilliseconds;
                break;
            }

            Thread.Sleep(5);
        }

        if (handleMs is null)
        {
            TryKill(process);
            throw new TimeoutException($"{options.TimeoutMs} ms 内没拿到可响应的主窗口（MainWindowHandle 始终为 0 或 Responding=false）");
        }

        process.Refresh();
        var workingSet = process.WorkingSet64;
        var privateBytes = process.PrivateMemorySize64;
        var peakWorkingSet = process.PeakWorkingSet64;

        TryKill(process);

        return new RunResult(index, handleMs.Value, ToMb(workingSet), ToMb(privateBytes), ToMb(peakWorkingSet), "ok", null);
    }

    private static void KillLeftovers(string exe)
    {
        var name = Path.GetFileNameWithoutExtension(exe);
        Process[] others;
        try
        {
            others = Process.GetProcessesByName(name);
        }
        catch
        {
            return;
        }

        if (others.Length == 0) return;

        Console.WriteLine($"[note] 关掉 {others.Length} 个同名残留进程（{name}）");
        foreach (var other in others)
        {
            try
            {
                if (other.Id == Environment.ProcessId) continue;
                other.CloseMainWindow();
                if (!other.WaitForExit(2000)) other.Kill(entireProcessTree: true);
            }
            catch
            {
                // 进程刚好自己退了：忽略
            }
            finally
            {
                other.Dispose();
            }
        }

        // 给单实例互斥锁一点释放时间，否则下一轮会直接弹「启动器已经在运行」
        Thread.Sleep(300);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch
        {
            // 已经退出
        }
    }

    // ---------------------------------------------------------------- 输出

    private static string FormatRow(RunResult run)
    {
        if (run.Status != "ok")
        {
            return string.Format(CultureInfo.InvariantCulture,
                "第 {0} 轮  {1,-7}  {2}", run.Index, "FAILED", run.Error);
        }

        return string.Format(CultureInfo.InvariantCulture,
            "第 {0} 轮  {1,5} ms   WS {2,7:F1} MB   Private {3,7:F1} MB   Peak {4,7:F1} MB",
            run.Index, run.HandleMs, run.WorkingSetMb, run.PrivateMb, run.PeakWorkingSetMb);
    }

    private static void PrintSummary(BenchSummary summary, int total)
    {
        Console.WriteLine($"=== 汇总（{summary.HandleMs.Count}/{total} 轮有效）===");
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "handleMs : 中位数 {0:F0}  最小 {1}  最大 {2}  平均 {3:F1}",
            summary.HandleMs.Median, summary.HandleMs.Min, summary.HandleMs.Max, summary.HandleMs.Mean));
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "内存 WS  : 中位数 {0:F1} MB  最小 {1:F1}  最大 {2:F1}",
            summary.WorkingSetMb.Median, summary.WorkingSetMb.Min, summary.WorkingSetMb.Max));
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "内存 Priv: 中位数 {0:F1} MB  最小 {1:F1}  最大 {2:F1}",
            summary.PrivateMb.Median, summary.PrivateMb.Min, summary.PrivateMb.Max));
    }

    private static void CompareWithBaseline(string baselinePath, BenchDocument current)
    {
        Console.WriteLine();
        Console.WriteLine("=== 与基线对比 ===");

        if (!File.Exists(baselinePath))
        {
            Console.Error.WriteLine($"基线文件不存在：{Path.GetFullPath(baselinePath)}");
            return;
        }

        BenchDocument? baseline;
        try
        {
            baseline = JsonSerializer.Deserialize<BenchDocument>(File.ReadAllText(baselinePath), JsonOptions);
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine("基线文件不是合法的 bench JSON：" + ex.Message);
            return;
        }

        if (baseline is null)
        {
            Console.Error.WriteLine("基线文件内容为空。");
            return;
        }

        var baseHandle = BaselineHandleMedian(baseline);
        var baseWorkingSet = BaselineWorkingSetMedian(baseline);
        if (baseHandle is null)
        {
            Console.Error.WriteLine("基线里没有有效的 handleMs 数据。");
            return;
        }

        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "基线 {0:yyyy-MM-dd HH:mm} · {1} 轮 · 中位数 {2:F0} ms",
            baseline.GeneratedAt, baseline.Runs.Count, baseHandle.Value));
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "当前 {0:yyyy-MM-dd HH:mm} · {1} 轮 · 中位数 {2:F0} ms",
            current.GeneratedAt, current.Runs.Count, current.Summary.HandleMs.Median));

        var delta = current.Summary.HandleMs.Median - baseHandle.Value;
        var percent = baseHandle.Value == 0 ? 0 : delta / baseHandle.Value * 100;
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "handleMs ：{0}{1:F0} ms（{2}{3:F1}%）{4}",
            delta >= 0 ? "+" : "", delta, delta >= 0 ? "+" : "", percent,
            delta > 50 ? "  ← 变慢" : delta < -50 ? "  ← 变快" : "  ← 基本持平"));

        if (baseWorkingSet is not null)
        {
            var memDelta = current.Summary.WorkingSetMb.Median - baseWorkingSet.Value;
            var memPercent = baseWorkingSet.Value == 0 ? 0 : memDelta / baseWorkingSet.Value * 100;
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "内存 WS ：{0}{1:F1} MB（{2}{3:F1}%）",
                memDelta >= 0 ? "+" : "", memDelta, memDelta >= 0 ? "+" : "", memPercent));
        }
    }

    private static double? BaselineHandleMedian(BenchDocument baseline)
    {
        if (baseline.Summary?.HandleMs?.Count > 0) return baseline.Summary.HandleMs.Median;

        var values = baseline.Runs.Where(r => r.Status == "ok").Select(r => (double)r.HandleMs).ToList();
        return values.Count == 0 ? null : Stats.From(values).Median;
    }

    private static double? BaselineWorkingSetMedian(BenchDocument baseline)
    {
        if (baseline.Summary?.WorkingSetMb?.Count > 0) return baseline.Summary.WorkingSetMb.Median;

        var values = baseline.Runs.Where(r => r.Status == "ok").Select(r => r.WorkingSetMb).ToList();
        return values.Count == 0 ? null : Stats.From(values).Median;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            启动器冷启动基准工具

            用法：
              launcher-bench --exe <MCLauncher.exe> [选项]

            选项：
              --exe <路径>        必填，要测的可执行文件（Release 产物）
              --runs <N>          轮数，默认 5
              --out <路径>        JSON 输出路径，默认 bench-<时间戳>.json
              --settle-ms <N>     每轮之间等待毫秒，默认 400
              --timeout-ms <N>    单轮等待主窗口的上限，默认 20000
              --baseline <路径>   与旧 JSON 对比，打印中位数差值与百分比
              --help              显示本帮助

            退出码：0 全部成功 / 1 有轮次失败 / 2 参数或路径错误
            """);
    }

    // ---------------------------------------------------------------- 小工具

    private static double ToMb(long bytes) => Math.Round(bytes / 1024.0 / 1024.0, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

// -------------------------------------------------------------------- 模型

internal sealed record BenchOptions(
    string Exe,
    int Runs,
    string OutPath,
    int SettleMs,
    int TimeoutMs,
    string? BaselinePath,
    bool ShowHelp)
{
    public static BenchOptions Parse(string[] args)
    {
        var exe = string.Empty;
        var runs = 5;
        string? outPath = null;
        var settleMs = 400;
        var timeoutMs = 20_000;
        string? baseline = null;
        var help = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string Next(string name)
            {
                if (i + 1 >= args.Length) throw new ArgumentException($"{name} 缺少取值");
                return args[++i];
            }

            switch (arg)
            {
                case "--help" or "-h" or "/?":
                    help = true;
                    break;
                case "--exe":
                    exe = Next("--exe");
                    break;
                case "--runs":
                    if (!int.TryParse(Next("--runs"), out runs) || runs <= 0)
                        throw new ArgumentException("--runs 需要正整数");
                    break;
                case "--out":
                    outPath = Next("--out");
                    break;
                case "--settle-ms":
                    if (!int.TryParse(Next("--settle-ms"), out settleMs) || settleMs < 0)
                        throw new ArgumentException("--settle-ms 需要非负整数");
                    break;
                case "--timeout-ms":
                    if (!int.TryParse(Next("--timeout-ms"), out timeoutMs) || timeoutMs <= 0)
                        throw new ArgumentException("--timeout-ms 需要正整数");
                    break;
                case "--baseline":
                    baseline = Next("--baseline");
                    break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException($"未知参数 {arg}");
                    if (exe.Length == 0) exe = arg;      // 允许把 exe 当第一个位置参数
                    else throw new ArgumentException($"多余的位置参数 {arg}");
                    break;
            }
        }

        if (!help && exe.Length == 0) throw new ArgumentException("必须给 --exe <MCLauncher.exe>");

        outPath ??= $"bench-{DateTime.Now:yyyyMMdd-HHmmss}.json";
        var fullExe = exe.Length == 0 ? string.Empty : Path.GetFullPath(exe);
        return new BenchOptions(fullExe, runs, outPath, settleMs, timeoutMs, baseline, help);
    }
}

internal sealed record BenchOptionsInfo(int Runs, int SettleMs, int TimeoutMs);

internal sealed record RunResult(
    int Index,
    long HandleMs,
    double WorkingSetMb,
    double PrivateMb,
    double PeakWorkingSetMb,
    string Status,
    string? Error)
{
    public static RunResult Failed(int index, string error) =>
        new(index, 0, 0, 0, 0, "failed", error);
}

internal sealed record Stats(int Count, double Min, double Median, double Max, double Mean)
{
    public static Stats From(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return new Stats(0, 0, 0, 0, 0);
        var sorted = values.OrderBy(v => v).ToArray();
        var mid = sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
        return new Stats(sorted.Length, sorted[0], mid, sorted[^1], values.Average());
    }
}

internal sealed record BenchSummary(Stats HandleMs, Stats WorkingSetMb, Stats PrivateMb, Stats PeakWorkingSetMb)
{
    public static BenchSummary From(IReadOnlyList<RunResult> runs)
    {
        var ok = runs.Where(r => r.Status == "ok").ToList();
        return new BenchSummary(
            Stats.From(ok.Select(r => (double)r.HandleMs).ToList()),
            Stats.From(ok.Select(r => r.WorkingSetMb).ToList()),
            Stats.From(ok.Select(r => r.PrivateMb).ToList()),
            Stats.From(ok.Select(r => r.PeakWorkingSetMb).ToList()));
    }
}

internal sealed record MachineInfo(
    string CpuName,
    int TotalRamMb,
    int ProcessorCount,
    string OsDescription,
    string OsVersion,
    string Architecture,
    string FrameworkDescription,
    string HostName,
    string UserName)
{
    public static MachineInfo Capture()
    {
        var memory = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        var ok = GlobalMemoryStatusEx(ref memory);
        return new MachineInfo(
            CpuName: ReadCpuName(),
            TotalRamMb: ok ? (int)(memory.TotalPhys / 1024 / 1024) : (int)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024),
            ProcessorCount: Environment.ProcessorCount,
            OsDescription: RuntimeInformation.OSDescription,
            OsVersion: Environment.OSVersion.VersionString,
            Architecture: RuntimeInformation.OSArchitecture.ToString(),
            FrameworkDescription: RuntimeInformation.FrameworkDescription,
            HostName: Environment.MachineName,
            UserName: Environment.UserName);
    }

    private static string ReadCpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var name = key?.GetValue("ProcessorNameString")?.ToString();
            return string.IsNullOrWhiteSpace(name) ? Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "?" : name.Trim();
        }
        catch
        {
            return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "?";
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }
}

internal sealed record TargetInfo(string Exe, string? FileVersion, string? ProductVersion, long SizeBytes, DateTimeOffset LastWriteUtc)
{
    public static TargetInfo Describe(string exe)
    {
        var info = new FileInfo(exe);
        FileVersionInfo? version = null;
        try
        {
            version = FileVersionInfo.GetVersionInfo(exe);
        }
        catch
        {
            // 读不到版本信息就当没有（不影响测量）
        }

        return new TargetInfo(
            exe,
            string.IsNullOrWhiteSpace(version?.FileVersion) ? null : version!.FileVersion,
            string.IsNullOrWhiteSpace(version?.ProductVersion) ? null : version!.ProductVersion,
            info.Length,
            info.LastWriteTimeUtc);
    }
}

internal sealed record BenchDocument(
    string Schema,
    DateTimeOffset GeneratedAt,
    MachineInfo Machine,
    TargetInfo Target,
    BenchOptionsInfo Options,
    IReadOnlyList<RunResult> Runs,
    BenchSummary Summary);
