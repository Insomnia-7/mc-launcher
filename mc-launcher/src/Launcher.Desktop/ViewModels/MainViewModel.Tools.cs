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

    private IEnumerable<string> CandidateGameRoots()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(profile, ".minecraft");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
        if (OperatingSystem.IsMacOS()) yield return Path.Combine(profile, "Library", "Application Support", "minecraft");
        yield return AppContext.BaseDirectory;
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
            foreach (var name in new[] { ".minecraft", "Minecraft", "MC", "PCL", "PCL2", "HMCL", "mclab", "Games" })
                yield return Path.Combine(drive.RootDirectory.FullName, name);
        }
    }
    [RelayCommand] private async Task AutoDetectGameRootAsync()
    {
        if (!CanRunTool()) return;
        OpenAppearance(); ShowsStorage = true; ShowsLibraryDiscovery = true;
        await FindLibrariesAsync(CandidateGameRoots(), 3);
    }

    /// <summary>只检索本机已有的 Java（PATH、常见安装目录、启动器数据目录），不下载。</summary>
    [RelayCommand]
    private async Task DetectLocalJavaAsync()
    {
        if (!CanRunTool()) return;

        IsToolBusy = true;
        ToolHint = null;

        try
        {
            StatusText = "正在检索本地 Java…";
            await LoadJavaOptionsAsync();

            var found = JavaOptions
                .Where(option => option.ExecutablePath is not null)
                .OrderBy(option => option.MajorVersion)
                .ToList();

            if (found.Count == 0)
            {
                StatusText = "没找到本地 Java。";
                ToolHint = "已查过 PATH、常见安装目录与启动器数据目录；启动游戏时仍会自动补一个。";
                return;
            }

            var majors = string.Join(" / ", found.Select(option => option.MajorVersion).Distinct());
            var current = SelectedJava?.Title ?? "自动选择";
            StatusText = $"找到 {found.Count} 个本地 Java（{majors}），当前：{current}";
            ToolHint = string.Join("；", found.Select(option => option.ExecutablePath).Distinct().Take(3));
        }
        catch (Exception ex)
        {
            StatusText = "检索 Java 失败：" + Launcher.Application.Diagnostics.ErrorText.Describe(ex);
        }
        finally
        {
            IsToolBusy = false;
        }
    }
}
