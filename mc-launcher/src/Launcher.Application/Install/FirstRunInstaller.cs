using Launcher.Application.Abstractions;
using Launcher.Application.Java;

namespace Launcher.Application.Install;

public sealed record FirstRunRequest(string VersionId, int MemoryMb);
/// <summary>
/// 安装向导的进度。
/// <para>
/// <see cref="CompletedBytes"/> / <see cref="TotalBytes"/> 是整批下载的累计值（0 表示这一步没有字节概念，
/// 例如「准备 Java」）。界面用它们算「已下载多少 MB、大概还要多久」，只有百分比时用户完全不知道
/// 这几百 MB 要走十分钟还是十秒钟。
/// </para>
/// </summary>
public sealed record FirstRunProgress(
    string Stage,
    double Percent,
    string? Detail = null,
    long CompletedBytes = 0,
    long TotalBytes = 0)
{
    /// <summary>这一步是否带字节进度。</summary>
    public bool HasBytes => TotalBytes > 0;

    public double CompletedMegabytes => CompletedBytes / 1024.0 / 1024.0;
    public double TotalMegabytes => TotalBytes / 1024.0 / 1024.0;
    public double RemainingMegabytes => Math.Max(0, TotalMegabytes - CompletedMegabytes);
}

/// <summary>原版安装向导：复用下载与 Java 管理，全部准备成功后才保存实例设置。</summary>
public sealed class FirstRunInstaller(
    GameInstaller installer,
    IVersionMetaClient meta,
    IJavaProvisioner javaProvisioner)
{
    public async Task InstallAsync(FirstRunRequest request, string gameRoot, string dataDirectory,
        IProgress<FirstRunProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.VersionId);
        if (request.VersionId is "." or ".." || request.VersionId.IndexOfAny(['/', '\\', ':']) >= 0
            || request.VersionId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("游戏版本号包含无效字符。", nameof(request));
        if (request.MemoryMb is < InstanceSettings.MinMemoryMb or > InstanceSettings.MaxMemoryMb)
            throw new ArgumentOutOfRangeException(nameof(request), "内存必须在 512–65536 MB 之间。");
        ct.ThrowIfCancellationRequested();

        progress?.Report(new("读取版本信息", 0));
        var version = await meta.GetVersionAsync(request.VersionId, ct).ConfigureAwait(false);
        if (!string.Equals(version.Id, request.VersionId, StringComparison.Ordinal))
            throw new InvalidDataException("版本信息与选择的版本不一致，请刷新版本列表后重试。");
        await installer.InstallVersionAsync(version, gameRoot, includeAssets: true,
            new ForwardProgress<DownloadProgress>(p => progress?.Report(
                new("下载游戏", p.Percent * 0.8, p.CurrentFile, p.CompletedBytes, p.TotalBytes))), ct).ConfigureAwait(false);

        var required = version.JavaVersion?.MajorVersion ?? 8;
        progress?.Report(new($"准备 Java {required}", 80));
        await javaProvisioner.EnsureAsync(required, dataDirectory,
            new ForwardProgress<JavaProvisionProgress>(p => progress?.Report(
                new($"准备 Java {required} · {p.Stage}", 80 + Math.Clamp(p.Percent, 0, 100) * 0.19, p.Detail))),
            ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        // 重试保留用户已配置的 Java、窗口、JVM 参数，只更新本次明确选择的内存。
        var settings = InstanceSettings.Load(gameRoot, request.VersionId) with { MemoryMb = request.MemoryMb };
        settings.Save(gameRoot, request.VersionId);
        progress?.Report(new("安装完成", 100));
    }

    private sealed class ForwardProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
