using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Launcher.Application.Abstractions;

namespace Launcher.Infrastructure.Security;

/// <summary>
/// Windows DPAPI 版加密：密钥由当前 Windows 用户登录凭据派生，不落盘。
/// 代价是换 Windows 用户、换机器、重装系统后旧文件解不开 —— 那时按"没登录过"重登即可。
/// </summary>
public sealed class DpapiSecretProtector(byte[]? entropy = null) : ISecretProtector
{
    /// <summary>附加熵：同一台机器上别的程序即便调 DPAPI，也解不出我们的存档。</summary>
    private static readonly byte[] DefaultEntropy = Encoding.UTF8.GetBytes("MCLauncher.Accounts.v1");

    private readonly byte[] _entropy = entropy ?? DefaultEntropy;

    /// <summary>DPAPI 只在 Windows 上可用；别处返回 null，调用方当作"没有加密能力"。</summary>
    public static DpapiSecretProtector? TryCreate()
    {
        if (!OperatingSystem.IsWindows()) return null;

        // 真的试一把：某些受限环境（服务账户 / AppContainer）会抛 PlatformNotSupportedException
        try
        {
            var probe = ProtectedData.Protect([1, 2, 3], DefaultEntropy, DataProtectionScope.CurrentUser);
            _ = ProtectedData.Unprotect(probe, DefaultEntropy, DataProtectionScope.CurrentUser);
            return new DpapiSecretProtector();
        }
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException or NotSupportedException)
        {
            return null;
        }
    }

    public byte[] Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI 只在 Windows 上可用");

        return ProtectCore(plaintext);
    }

    public string? TryUnprotect(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty) return null;
        if (!OperatingSystem.IsWindows()) return null;

        return UnprotectCore(payload.ToArray());
    }

    // 下面两个方法标了 SupportedOSPlatform，调用点必须先做 IsWindows 判断（CA1416 才认）。
    [SupportedOSPlatform("windows")]
    private byte[] ProtectCore(string plaintext) =>
        ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), _entropy, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private string? UnprotectCore(byte[] payload)
    {
        try
        {
            var plain = ProtectedData.Unprotect(payload, _entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException or NotSupportedException)
        {
            // 换了 Windows 用户 / 文件被改过 / 平台不支持：一律当解不开
            return null;
        }
    }

    public string Describe() => "DPAPI(CurrentUser)";
}
