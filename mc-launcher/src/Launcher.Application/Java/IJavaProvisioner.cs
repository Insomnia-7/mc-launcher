using Launcher.Domain.Java;

namespace Launcher.Application.Java;

/// <summary>Java 自动获取的阶段进度。Percent 只在当前阶段内有效。</summary>
public sealed record JavaProvisionProgress(string Stage, double Percent, string? Detail)
{
    public static JavaProvisionProgress Of(string stage, double percent = 0, string? detail = null) =>
        new(stage, percent, detail);
}

/// <summary>
/// 「缺 Java 就自己装一个」。启动器不能假设玩家装过 JDK：官方启动器就是把运行时装进自己的目录，
/// 我们也照做——放在 dataDirectory/java/&lt;major&gt;/ 下，跟用户自己装的 Java 完全隔离，
/// 卸载启动器时删这个目录就干净了，不会误伤系统 JAVA_HOME。
/// </summary>
public interface IJavaProvisioner
{
    /// <summary>确保本地有一个 >= majorVersion 的 Java，没有就下载到 dataDirectory/java/&lt;major&gt;/ 并返回。</summary>
    Task<JavaInstall> EnsureAsync(int majorVersion, string dataDirectory,
        IProgress<JavaProvisionProgress>? progress = null, CancellationToken ct = default);
}
