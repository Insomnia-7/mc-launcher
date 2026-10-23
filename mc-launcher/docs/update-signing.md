# 更新签名与发布密钥

## 验证流程

发布工具生成 update.json，随后产生同目录的 update.json.sig。签名覆盖清单的全部原始字节，包括版本号、入口、时间、说明、文件地址、大小与哈希。使用 .NET 自带 RSA-3072 / RSA-PSS / SHA-256，未引入新的启动器运行时依赖；原交接中 Ed25519 是建议，这里采用内置实现。

签名文件包含 schema=1、algorithm=rsa-pss-sha256、公钥 SPKI 的 SHA-256 作为 keyId 和 Base64 签名。受信公钥目录为 `src/Launcher.Application/Update/UpdateTrustKeys.json`，作为资源编入 Application 程序集；客户端不会信任远端清单提供的新公钥，也没有环境变量绕过验证的入口。

1. 主程序读取清单及相邻 .sig，验证成功后才显示更新。
2. 暂存前再次验证，并拒绝验证后被修改的内存清单。所有文件必须具备完整 SHA-256 与非负大小。
3. 暂存保留原始清单字节与签名，不重新序列化。文件下载继续校验大小和摘要。
4. 独立更新器在等待主程序退出、创建备份或替换文件之前再次验签，然后执行原有全量文件校验与替换回滚。

无签名、未知公钥、错误算法、损坏签名、内容篡改都会停止更新。校验失败原因在启动器状态栏显示；本地目录或 U 盘更新同样必须签名。原有 HTTP 地址兼容性保留，签名只提供来源真实性与完整性，不提供传输保密性，也不保证一定能获取最新版本。

## 私钥与自动发布

私钥保存在 GitHub `release-signing` 环境的 `MCLAUNCHER_UPDATE_SIGNING_KEY` Secret，值为 PKCS#8 DER 的 Base64。仅签名步骤注入该变量；其后独立验签不使用私钥。缺少私钥或公钥不匹配会中止发布。

发布环境仅允许 main 分支和 v* 标签。手工发布会检出指定的既有标签；标签必须符合版本格式。发布前运行构建、核心、架构及 WPF 检查，签名和独立验签成功后再上传资产。签名文件不进入被签名的文件列表。

本机另存 Windows DPAPI CurrentUser 加密备份，放在仓库之外；不生成持久化明文私钥。备份只能由对应 Windows 用户解密，复制密文到新机器不等于可恢复的异机备份。GitHub Secret 无法读回明文；迁移电脑前应在原用户环境下安排受保护的密钥迁移。

创建新密钥（不会覆盖现有备份）：

```powershell
pwsh -File scripts/new-update-key.ps1 -PrivateKeyPath '<仓库外目录>/update-signing.dpapi'
```

本机签名与独立验签：

```powershell
pwsh -File scripts/update-signing.ps1 -ManifestPath '<发布目录>/update.json' -PrivateKeyPath '<仓库外目录>/update-signing.dpapi'
pwsh -File scripts/update-signing.ps1 -ManifestPath '<发布目录>/update.json' -VerifyOnly
```

工具只打印公钥 ID 和结果，不打印私钥。公钥文件允许多条密钥；脚本会核对实际私钥的公钥是否已在目录中。

## 首次迁移与密钥轮换

旧的 v0.1.0 / v0.1.1 没有签名验证能力。本次不会修改这些标签、附件或发布新版本；首次安装签名功能应从可信项目发布页取得完整程序和独立更新器。

当前更新器会跳过替换正在运行的自身 exe。因此签名功能首次迁移和涉及公钥轮换时，必须完整替换更新器，不能仅依赖旧更新器的自更新。推荐先发布由旧密钥签名、同时内置新旧公钥的过渡安装包，确认主程序和更新器均安装后，再改用新私钥签名；后续完整版本再移除旧公钥。失窃或遗失密钥时应停止旧签名发布，通过可信发布页分发完整恢复版本。旧客户端的信任目录无法远程即时撤销。

更新清单签名不是 Windows 可执行文件的 Authenticode 签名，不能消除 SmartScreen 提示。

## 本轮验证

- Release 构建零错误、零警告；568 项核心检查通过，其中新增 29 项签名与暂存检查。
- 89 项 WPF 流程检查通过，新增签名错误可见且更新入口关闭；架构检查通过。
- 使用真实生成的发布密钥，PowerShell 签名与独立验签通过；CLI 暂存后由实际独立更新器验证并替换隔离测试文件，目标内容正确。
- 测试包括字节篡改、版本和摘要篡改、未知密钥、错误私钥/算法、无签名、非法暂存版本目录、校验值缺失、大小限制、取消、查询参数保留、原始字节保留及新旧公钥过渡。
- 测试使用隔离文件，没有覆盖真实安装、账号或游戏数据。GitHub Secret 已通过官方接口写入并读取元数据确认；本轮尚未创建新 Release 或在 GitHub 执行签名发布流水线。

## 参考

- [.NET RSA 签名 API](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.rsa.signdata)
- [GitHub 环境 Secret](https://docs.github.com/en/actions/concepts/workflows-and-actions/deployment-environments)
- [GitHub Secret 加密与管理接口](https://docs.github.com/en/rest/actions/secrets)
