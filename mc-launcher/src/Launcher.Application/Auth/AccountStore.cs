namespace Launcher.Application.Auth;

using Launcher.Application.Abstractions;

/// <summary>
/// 旧版「单账号存档」的兼容壳，内部全部转发到 <see cref="AccountLibrary"/>。
/// 保留它是因为 App / CLI 还在按老签名调用；新代码请直接用 AccountLibrary（多账号 + 离线身份），
/// 这层壳只是让旧调用点不用改就能继续跑，别再往里加新能力。
/// </summary>
public static class AccountStore
{
    /// <summary>旧文件名。新存档叫 accounts.json，这里只在迁移和清理时用得到。</summary>
    public const string FileName = AccountLibrary.LegacyFileName;

    public static string PathFor(string dataDirectory) => Path.Combine(dataDirectory, FileName);

    /// <summary>当前选中的正版账号；离线身份或没登录过时为 null。</summary>
    public static MicrosoftAccount? Load(string dataDirectory, ISecretProtector? protector = null) =>
        AccountLibrary.Load(dataDirectory, protector).Selected;

    /// <summary>存下一个账号并把它设为当前账号（等价于"登录成功"）。</summary>
    public static void Save(string dataDirectory, MicrosoftAccount account, ISecretProtector? protector = null)
    {
        var library = AccountLibrary.Load(dataDirectory, protector);
        library.AddOrUpdate(account);
        library.Save(dataDirectory, protector);
    }

    /// <summary>
    /// 退出当前账号：只摘掉选中项，其他账号留着（删完就退回离线身份）。
    /// 顺手删掉旧版 account.json——不删的话下次 Load 会当成"还没迁移过"再搬一次，等于没退出。
    /// </summary>
    public static void Clear(string dataDirectory, ISecretProtector? protector = null)
    {
        try
        {
            var library = AccountLibrary.Load(dataDirectory, protector);
            if (library.Selected is { } current)
            {
                library.Remove(current.Uuid);
                library.Save(dataDirectory, protector);
            }

            var legacy = PathFor(dataDirectory);
            if (File.Exists(legacy)) File.Delete(legacy);
        }
        catch
        {
            // 删不掉不影响：下次启动读到的是失效令牌，会在启动前引导重新登录
        }
    }
}
