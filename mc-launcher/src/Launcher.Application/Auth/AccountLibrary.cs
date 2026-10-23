using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Launcher.Application.Abstractions;

namespace Launcher.Application.Auth;

/// <summary>
/// 本地账号簿：多个正版账号 + 一个离线身份，外加"当前用哪个"。
/// 之所以不是单账号存档：一台机器上换号是常态（小号/朋友借号/双开），
/// 每换一次就重走一遍设备码登录太蠢，token 又都是长期有效的，存下来最省事。
/// 读档失败一律当空库——账号簿坏了不该让启动器起不来。
/// </summary>
public sealed class AccountLibrary
{
    /// <summary>多账号存档文件名。</summary>
    public const string FileName = "accounts.json";

    /// <summary>旧版单账号存档：只在没有 accounts.json 时读一次，读完不删（回退旧版本还能用）。</summary>
    public const string LegacyFileName = "account.json";

    /// <summary>
    /// 加密存档的魔数前缀。见到它就说明后面跟的是 <see cref="ISecretProtector"/> 的密文；
    /// 没有它则按明文 JSON 读（兼容旧存档，也兼容没配加密的 CLI / 测试）。
    /// </summary>
    private static readonly byte[] SecretMagic = "MCLA1"u8.ToArray();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,   // 老存档是 PascalCase，新存档是小写，读的时候两边都认
    };

    private readonly List<MicrosoftAccount> _accounts;
    private readonly ReadOnlyCollection<MicrosoftAccount> _view;
    private OfflineIdentity _offline;

    // 存的是 uuid 而不是对象引用：Removed/替换后不会留下指向旧对象的幽灵选中项
    private string? _selectedUuid;

    private AccountLibrary(List<MicrosoftAccount> accounts, string? selectedUuid, string offlineName)
    {
        _accounts = accounts;
        _view = accounts.AsReadOnly();

        var safeName = OfflineIdentityFactory.IsValidName(offlineName)
            ? offlineName
            : OfflineIdentityFactory.DefaultName;
        _offline = OfflineIdentityFactory.Create(safeName);

        _selectedUuid = ResolveSelection(accounts, selectedUuid);
    }

    /// <summary>存档里所有可用账号，按写入顺序。</summary>
    public IReadOnlyList<MicrosoftAccount> Accounts => _view;

    /// <summary>当前选中的正版账号；null 表示用离线身份玩。</summary>
    public MicrosoftAccount? Selected => Find(_selectedUuid);

    /// <summary>离线身份。永远有值：没配过就是默认名 <c>Developer</c>。</summary>
    public OfflineIdentity OfflineIdentity => _offline;

    public static string PathFor(string dataDirectory) => Path.Combine(dataDirectory, FileName);

    /// <summary>
    /// 读账号簿。优先 accounts.json；没有就尝试从旧 account.json 迁移（此时会顺手落盘一次）。
    /// 传了 <paramref name="protector"/> 且存档带加密前缀时会尝试解密；解不开等同于没有存档。
    /// 任何 IO / JSON 异常都吞掉并返回空库，绝不把异常抛给调用方。
    /// </summary>
    public static AccountLibrary Load(string dataDirectory, ISecretProtector? protector = null)
    {
        var accounts = new List<MicrosoftAccount>();
        string? selectedUuid = null;
        var offlineName = OfflineIdentityFactory.DefaultName;
        var migratedFromLegacy = false;
        var encryptOnLoad = false;

        try
        {
            var path = PathFor(dataDirectory);
            if (File.Exists(path))
            {
                var file = ReadAccountsFile(path, protector, out var wasPlaintext);
                if (file is not null)
                {
                    foreach (var stored in file.Accounts ?? [])
                    {
                        var account = stored?.ToAccount();
                        if (IsUsable(account)) Merge(accounts, account!);
                    }

                    encryptOnLoad = wasPlaintext && protector is not null;
                    selectedUuid = file.Selected;
                    offlineName = file.OfflineName ?? offlineName;
                }
            }
            else
            {
                var legacy = ReadLegacyAccount(dataDirectory);
                if (legacy is not null)
                {
                    Merge(accounts, legacy);
                    selectedUuid = legacy.Uuid;
                    migratedFromLegacy = true;
                }
            }
        }
        catch
        {
            // 权限不足、JSON 写坏、磁盘拔了……都当成"没登录过"，不让它挡住启动
        }

        var library = new AccountLibrary(accounts, selectedUuid, offlineName);

        if (migratedFromLegacy || encryptOnLoad)
        {
            // 两种情况要顺手重写一次：① 从旧 account.json 迁过来；② 明文存档遇到了可用的加密器（升到密文）。
            // 写盘失败不影响本次内存态，下次启动再试。
            try { library.Save(dataDirectory, protector); } catch { /* 下次启动再迁 */ }
        }

        return library;
    }

    /// <summary>
    /// 读存档体：带 <c>MCLA1</c> 前缀当密文解，否则当明文 JSON。
    /// 有前缀但这次没有解密能力 / 解不开 → 返回 null（调用方按"没登录过"处理）。
    /// </summary>
    private static AccountsFile? ReadAccountsFile(string path, ISecretProtector? protector, out bool wasPlaintext)
    {
        wasPlaintext = false;
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length == 0) return null;

        if (bytes.AsSpan().StartsWith(SecretMagic))
        {
            if (protector is null) return null;

            var json = protector.TryUnprotect(bytes.AsSpan(SecretMagic.Length));
            return json is null ? null : JsonSerializer.Deserialize<AccountsFile>(json, Options);
        }

        wasPlaintext = true;
        return JsonSerializer.Deserialize<AccountsFile>(Encoding.UTF8.GetString(bytes), Options);
    }

    /// <summary>
    /// 落盘。先写临时文件再整体替换：多账号清单一旦写坏，用户所有 token 都得重登，代价太大。
    /// 传了 <paramref name="protector"/> 就写「魔数 + 密文」，否则写明文 JSON（兼容 CLI / 测试）。
    /// </summary>
    public void Save(string dataDirectory, ISecretProtector? protector = null)
    {
        Directory.CreateDirectory(dataDirectory);

        var payload = new AccountsFile(
            _selectedUuid,
            _offline.Name,
            [.. _accounts.Select(StoredAccount.From)]);

        var path = PathFor(dataDirectory);
        var temp = path + ".tmp";
        var json = JsonSerializer.Serialize(payload, Options);

        try
        {
            if (protector is null)
            {
                File.WriteAllText(temp, json);
            }
            else
            {
                var cipher = protector.Protect(json);
                var buffer = new byte[SecretMagic.Length + cipher.Length];
                SecretMagic.CopyTo(buffer, 0);
                cipher.CopyTo(buffer, SecretMagic.Length);
                File.WriteAllBytes(temp, buffer);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* 残留 tmp 只是难看 */ }
        }
    }

    /// <summary>
    /// 按 uuid 去重后写入，并自动选中它——调用方拿到新账号的下一步一定是用它启动。
    /// </summary>
    public void AddOrUpdate(MicrosoftAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (!IsUsable(account))
            throw new ArgumentException("账号缺少 uuid / 名字 / refresh_token，存下来也用不了。", nameof(account));

        var index = IndexOf(account.Uuid);
        if (index >= 0) _accounts[index] = account;
        else _accounts.Add(account);

        _selectedUuid = _accounts[IndexOf(account.Uuid)].Uuid;
    }

    /// <summary>删掉一个账号；删的正好是当前账号时自动退回离线身份。</summary>
    public bool Remove(string uuid)
    {
        if (string.IsNullOrWhiteSpace(uuid)) return false;

        var index = IndexOf(uuid);
        if (index < 0) return false;

        _accounts.RemoveAt(index);
        if (string.Equals(_selectedUuid, uuid, StringComparison.OrdinalIgnoreCase)) _selectedUuid = null;
        return true;
    }

    /// <summary>
    /// 切换当前账号。传 null = 用离线身份。
    /// 传了列表里没有的 uuid 就当没听见：界面只能点列表项，真传错了也不该让登录态炸掉。
    /// </summary>
    public void Select(string? uuid)
    {
        if (uuid is null)
        {
            _selectedUuid = null;
            return;
        }

        var account = Find(uuid);
        if (account is not null) _selectedUuid = account.Uuid;
    }

    /// <summary>改离线用户名；UUID 跟着重算（名字非法会抛 <see cref="ArgumentException"/>，界面应先校验）。</summary>
    public void SetOfflineName(string name) => _offline = OfflineIdentityFactory.Create(name);

    private MicrosoftAccount? Find(string? uuid)
    {
        if (string.IsNullOrWhiteSpace(uuid)) return null;
        var index = IndexOf(uuid);
        return index < 0 ? null : _accounts[index];
    }

    private int IndexOf(string? uuid)
    {
        if (string.IsNullOrWhiteSpace(uuid)) return -1;

        for (var i = 0; i < _accounts.Count; i++)
        {
            if (string.Equals(_accounts[i].Uuid, uuid, StringComparison.OrdinalIgnoreCase)) return i;
        }

        return -1;
    }

    /// <summary>把选中项收敛到"确实存在的账号"，并统一用存档里那份 uuid 的大小写。</summary>
    private static string? ResolveSelection(List<MicrosoftAccount> accounts, string? selectedUuid)
    {
        if (string.IsNullOrWhiteSpace(selectedUuid)) return null;

        foreach (var account in accounts)
        {
            if (string.Equals(account.Uuid, selectedUuid, StringComparison.OrdinalIgnoreCase)) return account.Uuid;
        }

        return null;
    }

    private static void Merge(List<MicrosoftAccount> accounts, MicrosoftAccount account)
    {
        for (var i = 0; i < accounts.Count; i++)
        {
            if (string.Equals(accounts[i].Uuid, account.Uuid, StringComparison.OrdinalIgnoreCase))
            {
                accounts[i] = account;
                return;
            }
        }

        accounts.Add(account);
    }

    /// <summary>没 uuid 认不出人，没 refresh_token 过了 24 小时就彻底用不了，这两类不入库。</summary>
    private static bool IsUsable(MicrosoftAccount? account) =>
        account is not null &&
        !string.IsNullOrWhiteSpace(account.Uuid) &&
        !string.IsNullOrWhiteSpace(account.Name) &&
        !string.IsNullOrWhiteSpace(account.RefreshToken);

    /// <summary>旧版单账号存档：一个裸的 <see cref="MicrosoftAccount"/> 对象。</summary>
    private static MicrosoftAccount? ReadLegacyAccount(string dataDirectory)
    {
        try
        {
            var path = Path.Combine(dataDirectory, LegacyFileName);
            if (!File.Exists(path)) return null;

            var account = JsonSerializer.Deserialize<MicrosoftAccount>(File.ReadAllText(path), Options);
            return IsUsable(account) ? account : null;
        }
        catch
        {
            return null;
        }
    }

    private sealed record AccountsFile(
        [property: JsonPropertyName("selected")] string? Selected,
        [property: JsonPropertyName("offlineName")] string? OfflineName,
        [property: JsonPropertyName("accounts")] List<StoredAccount?>? Accounts);

    /// <summary>
    /// 落盘用的形状。不直接序列化 <see cref="MicrosoftAccount"/>：它有 IsExpired / NeedsRefresh
    /// 这类计算属性，写进去就是垃圾数据，而且字段命名要稳定，不能跟着模型改。
    /// </summary>
    private sealed record StoredAccount(
        [property: JsonPropertyName("uuid")] string? Uuid,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("accessToken")] string? AccessToken,
        [property: JsonPropertyName("refreshToken")] string? RefreshToken,
        [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt)
    {
        public static StoredAccount From(MicrosoftAccount account) =>
            new(account.Uuid, account.Name, account.AccessToken, account.RefreshToken, account.ExpiresAt);

        public MicrosoftAccount? ToAccount() =>
            string.IsNullOrWhiteSpace(Uuid)
                ? null
                : new MicrosoftAccount(Uuid!, Name ?? Uuid!, AccessToken ?? "", RefreshToken ?? "", ExpiresAt);
    }
}
