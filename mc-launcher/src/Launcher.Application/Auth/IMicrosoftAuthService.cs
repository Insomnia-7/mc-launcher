namespace Launcher.Application.Auth;

/// <summary>
/// 微软账号登录。走 OAuth 2.0 设备码（device code），这是所有第三方启动器
/// 唯一不需要 client secret、也不需要内嵌浏览器的合法路径。
/// </summary>
public interface IMicrosoftAuthService
{
    /// <summary>
    /// 申请设备码。调用方拿到 <see cref="DeviceCodeSession"/> 后要把
    /// <c>VerificationUri</c> 和 <c>UserCode</c> 显示给用户，然后调 <see cref="WaitForLoginAsync"/>。
    /// </summary>
    Task<DeviceCodeSession> StartDeviceCodeAsync(CancellationToken ct = default);

    /// <summary>
    /// 轮询直到用户在浏览器里完成授权，再串完 Xbox Live → XSTS → Minecraft 换证。
    /// 用户取消 / 会话过期 / 账号被拒都会抛带中文原因的 <see cref="InvalidOperationException"/>。
    /// </summary>
    /// <param name="status">给界面滚动提示用，可能被调用多次；不需要就传 null。</param>
    Task<MicrosoftAccount> WaitForLoginAsync(
        DeviceCodeSession session,
        IProgress<string>? status = null,
        CancellationToken ct = default);

    /// <summary>
    /// 用 refresh_token 静默续期。微软的 refresh_token 是一次性的，返回的账号里是新的那个，
    /// 调用方必须拿返回值整体替换旧账号，不能继续用传入的 <paramref name="account"/>。
    /// </summary>
    Task<MicrosoftAccount> RefreshAsync(MicrosoftAccount account, CancellationToken ct = default);
}
