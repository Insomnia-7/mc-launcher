# GitHub 发布约定

用户于 2026-11-04 明确要求：每次改动更新均同步 GitHub 并发布。此要求替代早期“仅本地交付，体验确认后再发布”的安排。

一批功能或修复完成后，执行相关验证并交付以下内容：

1. 提交源码并推送到 `Insomnia-7/mc-launcher`，先核对远端最新提交，保留其他贡献者的工作。
2. 更新 `CHANGELOG.md` 和 `docs/releases/history.json`，记录实际版本、源码提交、文件大小、SHA-256、变更和验证范围。
3. 将 `desktop-v<版本>` 标签指向实际构建所用提交。文档可在后续提交补录，但标签与 EXE 的 ProductVersion 后缀必须相符。
4. 创建新 Release，上传 Windows 单文件 `Fangyu.exe`、`update.json`、`update.json.sig`、校验文件和使用说明；签名清单必须引用实际文件的大小与哈希。
5. 草稿资产上传完成并核对远端哈希后，公开发布。预览版按用户于 2026-11-15 确认的安排标记为 Pre-release；正式版才设为 Latest。公开版本不覆盖重打，必要时递增版本。
6. 验证 GitHub 最新下载入口、生产更新客户端的版本判断和签名、下载文件哈希；最终回复提供真实 Release 链接。

正式版 Windows 更新由 `releases/latest/download/update.json` 提供。Linux/macOS 使用同一最新发行页中的分平台清单。Windows 单独更新时保留其他平台上次已发布且签名有效的清单与原始链接，明确说明其版本仍未变更，避免已安装客户端遇到 404。禁止将旧平台程序标为新版本。

只发布程序和公开验证材料。账号、游戏数据、本机路径配置、凭据及签名私钥不得上传。签名使用既有受信密钥，不为发布临时替换客户端公钥。

预览版本可在 Releases 中直接下载，使用该版本标签下的 `update.json` 与 `update.json.sig` 验证更新。尚无正式版时，GitHub 的 `releases/latest` 返回 404；不把预览版改标为正式版来占用该入口。
