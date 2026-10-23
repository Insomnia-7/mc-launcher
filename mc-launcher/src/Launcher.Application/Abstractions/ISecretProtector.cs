namespace Launcher.Application.Abstractions;

/// <summary>
/// 把一段文本加密成字节、再解回来。Application 层不关心算法（Windows 上是 DPAPI），
/// 只约定两件事：<see cref="Protect"/> 不返回空；<see cref="TryUnprotect"/> 解不开就返回 null。
/// 解不开绝不能抛异常——账号簿坏掉不该让启动器起不来。
/// </summary>
public interface ISecretProtector
{
    /// <summary>加密明文，返回可直接写盘的字节。</summary>
    byte[] Protect(string plaintext);

    /// <summary>尝试解密；密钥不对 / 换过 Windows 用户 / 文件损坏都返回 null。</summary>
    string? TryUnprotect(ReadOnlySpan<byte> payload);

    /// <summary>给人看的名字，写进日志用（例如 "DPAPI(CurrentUser)"）。</summary>
    string Describe();
}
