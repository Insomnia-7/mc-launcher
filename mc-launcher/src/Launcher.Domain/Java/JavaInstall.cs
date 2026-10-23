namespace Launcher.Domain.Java;

/// <summary>一台可用的 Java。MajorVersion 是规范化后的主版本号（1.8 记为 8）。</summary>
public sealed record JavaInstall(
    string ExecutablePath,
    int MajorVersion,
    string VersionString,
    string Vendor,
    string Arch)
{
    public string? Home => Path.GetDirectoryName(Path.GetDirectoryName(ExecutablePath));

    public override string ToString() => $"Java {MajorVersion} ({Vendor}) @ {ExecutablePath}";
}
