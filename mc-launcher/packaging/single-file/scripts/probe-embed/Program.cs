using System.Diagnostics;
using System.Reflection;

// 单文件模式下的更新器落地方式：把编进主程序的 mclupd.exe 解到 %TEMP% 再跑。
// 这段逻辑和 drafts/src-patch.md 里给 MainViewModel 的补丁一致，用来独立验证
// 「嵌入 → 解出 → 执行」这条路，不需要真的去点主程序的「更新」按钮。
//
// 它不在 solution 里，只是本目录的测试夹具：
//   dotnet publish probe-embed.csproj -c Release -p:UpdaterPayload=<mclupd.exe 的路径> -o out
const string ResourceName = "Launcher.Updater.mclupd.exe";

string? staging = null, target = null, restart = null;
int? waitPid = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--staging" when i + 1 < args.Length: staging = args[++i]; break;
        case "--target" when i + 1 < args.Length: target = args[++i]; break;
        case "--restart" when i + 1 < args.Length: restart = args[++i]; break;
        case "--wait-pid" when i + 1 < args.Length: waitPid = int.Parse(args[++i]); break;
    }
}

if (staging is null || target is null)
{
    Console.Error.WriteLine("用法：probe-embed --staging <dir> --target <dir> [--restart <exe>] [--wait-pid <pid>]");
    return 1;
}

using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
if (stream is null)
{
    Console.Error.WriteLine("嵌入资源缺失：" + ResourceName);
    return 9;
}

var length = stream.Length;
var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0";
var directory = Path.Combine(Path.GetTempPath(), "mclauncher-updater", $"{version}-{length}");
Directory.CreateDirectory(directory);
var updaterPath = Path.Combine(directory, "mclupd.exe");
if (!File.Exists(updaterPath) || new FileInfo(updaterPath).Length != length)
{
    using var file = File.Create(updaterPath);
    stream.CopyTo(file);
}
Console.WriteLine($"[probe] 嵌入资源 {length / 1024 / 1024.0:F1} MB → {updaterPath}");

var startInfo = new ProcessStartInfo(updaterPath) { UseShellExecute = false };
startInfo.ArgumentList.Add("--staging"); startInfo.ArgumentList.Add(staging);
startInfo.ArgumentList.Add("--target"); startInfo.ArgumentList.Add(target);
if (restart is not null) { startInfo.ArgumentList.Add("--restart"); startInfo.ArgumentList.Add(restart); }
if (waitPid is { } pid) { startInfo.ArgumentList.Add("--wait-pid"); startInfo.ArgumentList.Add(pid.ToString()); }

using var process = Process.Start(startInfo)!;
await process.WaitForExitAsync();
Console.WriteLine($"[probe] mclupd 退出码 {process.ExitCode}");
return process.ExitCode;
