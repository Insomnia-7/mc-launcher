using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.Application.Java;
using Launcher.Domain.Java;
using Microsoft.Extensions.Logging;

namespace Launcher.App.ViewModels;

/// <summary>
/// 桌面版游戏库里的两个快捷动作：自动找游戏目录、自动装 Java。
/// 都放在自己的忙碌标志里，避免和「启动游戏」抢 IsBusy。
/// </summary>
public sealed partial class MainViewModel
{
    [ObservableProperty] private bool _isToolBusy;
    [ObservableProperty] private string? _toolHint;

    private bool CanRunTool() => !IsToolBusy && !IsBusy;

    /// <summary>常见安装位置：各固定盘符下的 .minecraft / 启动器目录，以及当前用户的 .minecraft。</summary>
    private static readonly string[] CommonFolderNames =
    [
        ".minecraft", "minecraft", "Minecraft", "mclab", "MC",
        "PCL", "PCL2", "HMCL", "Games\\.minecraft", "Games\\Minecraft", "游戏\\.minecraft",
    ];

    private static bool LooksLikeGameRoot(string path)
    {
        try
        {
            var versions = Path.Combine(path, "versions");
            if (!Directory.Exists(versions)) return false;

            // 至少要有一个「目录里有同名 json」的版本，空壳目录不算。
            foreach (var directory in Directory.EnumerateDirectories(versions))
            {
                var id = Path.GetFileName(directory);
                if (File.Exists(Path.Combine(directory, id + ".json"))) return true;
            }
        }
        catch (Exception)
        {
            // 无权限、盘符消失都只是「这个候选不成立」，不该中断扫描。
        }

        return false;
    }

    private IEnumerable<string> CandidateGameRoots()
    {
        var fromProfile = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(fromProfile))
        {
            yield return Path.Combine(fromProfile, ".minecraft");
        }

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
            if (!drive.IsReady) continue;

            foreach (var name in CommonFolderNames)
            {
                var folder = Path.Combine(drive.RootDirectory.FullName, name);
                yield return folder;
                // 常见形态是「启动器目录\.minecraft」，两层都要试。
                yield return Path.Combine(folder, ".minecraft");
            }
        }
    }

    [RelayCommand]
    private async Task AutoDetectGameRootAsync()
    {
        // 按钮本身用 IsEnabled 绑 IsToolBusy，这里再兜一层，避免并发点两下。
        if (!CanRunTool()) return;

        IsToolBusy = true;
        ToolHint = null;
        StatusText = "正在扫描常见位置的游戏目录…";

        try
        {
            var current = Path.GetFullPath(GameRoot);
            var found = await Task.Run(() => CandidateGameRoots()
                .Select(path =>
                {
                    try { return Path.GetFullPath(path); }
                    catch (Exception) { return null; }
                })
                .Where(path => path is not null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(path => !string.Equals(path, current, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(path => LooksLikeGameRoot(path!)));

            if (found is null)
            {
                StatusText = "没找到现成的游戏目录。可以点「添加游戏版本」新建一个，或用安装向导指定目录。";
                ToolHint = "已扫描各盘符下的 .minecraft / mclab / PCL / HMCL 等常见位置。";
                return;
            }

            if (TryUseGameRoot(found, out var error))
            {
                StatusText = $"找到并切换到 {found}（{Instances.Count} 个版本）";
                ToolHint = null;
            }
            else
            {
                StatusText = $"找到了 {found}，但无法使用：{error}";
                ToolHint = null;
            }
        }
        catch (Exception ex)
        {
            StatusText = "自动搜索失败：" + Launcher.Application.Diagnostics.ErrorText.Describe(ex);
        }
        finally
        {
            IsToolBusy = false;
        }
    }

    [RelayCommand]
    private async Task ProvisionJavaAsync()
    {
        if (!CanRunTool()) return;

        IsToolBusy = true;
        ToolHint = null;

        try
        {
            var required = 21;
            if (SelectedInstance is { } instance)
            {
                try
                {
                    var version = await _versionLoader.LoadAsync(GameRoot, instance.VersionId);
                    required = version.JavaVersion?.MajorVersion ?? required;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "读取版本 {Version} 的 Java 要求失败，按 {Major} 处理", instance.VersionId, required);
                }
            }

            if (JavaOptions.Any(option => option.ExecutablePath is not null && option.MajorVersion >= required))
            {
                StatusText = $"本机已经有 Java {required}+ 了，不需要再下载";
                return;
            }

            StatusText = $"正在准备 Java {required}（首次要下几十 MB）…";
            var progress = new Progress<JavaProvisionProgress>(p =>
                StatusText = p.Detail is { Length: > 0 }
                    ? $"Java {required} · {p.Stage} {p.Percent:0}% · {p.Detail}"
                    : $"Java {required} · {p.Stage} {p.Percent:0}%");

            var java = await _javaProvisioner.EnsureAsync(required, AppPaths.DataDirectory, progress);
            await LoadJavaOptionsAsync();
            StatusText = $"Java {java.MajorVersion} 已就绪：{java.ExecutablePath}";
        }
        catch (NotSupportedException ex)
        {
            StatusText = ex.Message;
        }
        catch (Exception ex)
        {
            StatusText = "获取 Java 失败：" + Launcher.Application.Diagnostics.ErrorText.Describe(ex);
        }
        finally
        {
            IsToolBusy = false;
        }
    }
}
