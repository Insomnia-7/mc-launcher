using Launcher.Application.Abstractions;
using Launcher.Application.Java;
using Launcher.Domain.Java;

namespace Launcher.Desktop;

// Until the shared downloader supports Unix archives, never install its Windows JRE on another OS.
internal sealed class ManualJavaProvisioner : IJavaProvisioner
{
    public Task<JavaInstall> EnsureAsync(int majorVersion,string dataDirectory,IProgress<JavaProvisionProgress>? progress=null,CancellationToken ct=default)
        => Task.FromException<JavaInstall>(new NotSupportedException($"请先在此系统安装 Java {majorVersion}，并设置 JAVA_HOME 或加入 PATH；当前预览版的 Java 自动下载只适用于 Windows。"));
}
