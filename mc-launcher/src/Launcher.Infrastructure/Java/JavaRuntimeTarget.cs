using System.Runtime.InteropServices;

namespace Launcher.Infrastructure.Java;

public sealed record JavaRuntimeTarget(string Os, string Architecture)
{
    public static JavaRuntimeTarget Current => Resolve(OperatingSystem.IsWindows()?"windows":OperatingSystem.IsMacOS()?"mac":"linux",RuntimeInformation.ProcessArchitecture.ToString());
    public static JavaRuntimeTarget Resolve(string os,string architecture) => new(
        os is "windows" or "linux" or "mac" ? os : throw new PlatformNotSupportedException("不支持的 Java 系统。"),
        architecture.ToLowerInvariant() switch { "x64"=>"x64", "arm64" or "aarch64"=>"aarch64", _=>throw new PlatformNotSupportedException("目前支持 x64 和 ARM64 Java。") });
    public string Executable => Os=="windows"?"java.exe":"java";
    public string PackageExtension => Os=="windows"?".zip":".tar.gz";
    public string AssetUrl(int major) => $"https://api.adoptium.net/v3/assets/latest/{major}/hotspot?os={Os}&architecture={Architecture}&image_type=jre&vendor=eclipse";
}
