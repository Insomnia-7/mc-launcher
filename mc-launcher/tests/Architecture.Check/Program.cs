// ADR-001 护栏：Core 层不得沾染 Windows/WPF 专有 API。
// 零依赖控制台程序，退出码 1 = 违反约束，可用于 CI / 手动巡检。
using System.Reflection;
using System.Runtime.InteropServices;

var failures = new List<string>();

// ---------- 1. 程序集级：Core 不得引用 UI / Windows 专有程序集 ----------
string[] forbiddenAssemblies =
[
    "System.Windows", "System.Windows.Forms", "PresentationFramework", "PresentationCore",
    "WindowsBase", "System.Drawing", "System.Drawing.Common", "Microsoft.Win32.Registry",
    "Microsoft.WindowsDesktop.App", "WindowsFormsIntegration"
];

Assembly[] coreAssemblies =
[
    typeof(Launcher.Domain.Versions.VersionJson).Assembly,
    typeof(Launcher.Application.Versions.VersionResolver).Assembly
];

foreach (var asm in coreAssemblies)
{
    foreach (var reference in asm.GetReferencedAssemblies())
    {
        if (forbiddenAssemblies.Contains(reference.Name, StringComparer.OrdinalIgnoreCase))
            failures.Add($"[assembly] {asm.GetName().Name} 引用了被禁程序集 {reference.Name}");
    }
}

// ---------- 2. 类型级：Core 不得出现 Windows 命名空间类型 ----------
string[] forbiddenNamespaces = ["System.Windows", "Microsoft.Win32", "System.Drawing", "System.Windows.Forms"];

foreach (var asm in coreAssemblies)
{
    foreach (var type in asm.GetTypes())
    {
        var ns = type.Namespace ?? string.Empty;
        if (forbiddenNamespaces.Any(f => ns.Equals(f, StringComparison.Ordinal) || ns.StartsWith(f + ".", StringComparison.Ordinal)))
            failures.Add($"[type] {asm.GetName().Name}!{type.FullName} 位于被禁命名空间 {ns}");
    }
}

// ---------- 3. P/Invoke 级：Core 不得直连 Win32 ----------
foreach (var asm in coreAssemblies)
{
    foreach (var type in asm.GetTypes())
    {
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (method.GetCustomAttribute<DllImportAttribute>() is { } dll)
                failures.Add($"[pinvoke] {asm.GetName().Name}!{type.FullName}.{method.Name} 直连 {dll.Value}");
        }
    }
}

// ---------- 4. 项目文件级：TFM 与 UseWPF 纪律 ----------
var root = FindRepositoryRoot();
if (root is null)
{
    Console.Error.WriteLine("[x] 找不到 MinecraftLauncher.sln，无法检查 csproj");
    return 2;
}

var coreProjects = new[]
{
    Path.Combine(root, "src", "Launcher.Domain", "Launcher.Domain.csproj"),
    Path.Combine(root, "src", "Launcher.Application", "Launcher.Application.csproj"),
    Path.Combine(root, "src", "Launcher.Infrastructure", "Launcher.Infrastructure.csproj")
};

foreach (var project in coreProjects)
{
    if (!File.Exists(project))
    {
        failures.Add($"[csproj] 缺少 {Path.GetFileName(project)}");
        continue;
    }

    var text = File.ReadAllText(project);
    var name = Path.GetFileNameWithoutExtension(project);

    if (!text.Contains("<TargetFramework>net10.0</TargetFramework>", StringComparison.Ordinal))
        failures.Add($"[csproj] {name} 的 TargetFramework 必须是 net10.0（不得 -windows）");

    if (text.Contains("UseWPF", StringComparison.OrdinalIgnoreCase))
        failures.Add($"[csproj] {name} 不得启用 UseWPF");

    if (text.Contains("UseWindowsForms", StringComparison.OrdinalIgnoreCase))
        failures.Add($"[csproj] {name} 不得启用 UseWindowsForms");
}

// ---------- 结果 ----------
if (failures.Count == 0)
{
    Console.WriteLine($"[ok] ADR-001 护栏通过：{coreAssemblies.Length} 个 Core 程序集 + {coreProjects.Length} 个 csproj 均无 Windows/WPF 污染");
    return 0;
}

Console.Error.WriteLine($"[FAIL] 发现 {failures.Count} 处违反 ADR-001：");
foreach (var failure in failures) Console.Error.WriteLine("  " + failure);
return 1;

static string? FindRepositoryRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "MinecraftLauncher.sln"))) return dir.FullName;
        dir = dir.Parent;
    }
    return null;
}
