using System.Runtime.InteropServices;

namespace Launcher.Application.Update;

public sealed record DesktopUpdateLayout(string Target,string Entry,string Runtime)
{
    public static string CurrentRuntime => (OperatingSystem.IsWindows()?"win":OperatingSystem.IsMacOS()?"osx":"linux")+"-"+RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
    public static string FeedName => CurrentRuntime=="win-x64"?"update.json":$"update-{CurrentRuntime}.json";
    public static DesktopUpdateLayout Resolve(string executable,string? runtime=null)
    {
        var rid=runtime??CurrentRuntime;var full=Path.GetFullPath(executable);var folder=Path.GetDirectoryName(full)!;
        if(Path.GetFileNameWithoutExtension(full).Equals("dotnet",StringComparison.OrdinalIgnoreCase))throw new IOException("开发运行请使用发布页安装包更新。");
        if(rid.StartsWith("osx-",StringComparison.Ordinal))
        {
            if(Path.GetFileName(folder)!="MacOS"||Path.GetFileName(Path.GetDirectoryName(folder))!="Contents")throw new IOException("macOS 自更新需要完整的方隅.app 安装包。");
            return new(Path.GetDirectoryName(folder)!,"MacOS/"+Path.GetFileName(full),rid);
        }
        return new(folder,Path.GetFileName(full),rid);
    }
    public static void EnsureExecutable(string path)
    {if(!OperatingSystem.IsWindows())File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute|UnixFileMode.GroupRead|UnixFileMode.GroupExecute|UnixFileMode.OtherRead|UnixFileMode.OtherExecute);}
}
