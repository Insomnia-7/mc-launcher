# 启动器自动更新（0.7.0-preview.1）

本版开始接通 Avalonia 单文件版的启动器更新，不更换界面框架，不增加浏览器或额外运行时依赖。0.6 及更早的桌面版本需要先手动下载一次本版，之后才能接收应用内提示。

## 使用

设置 → 关于与更新，显示当前完整预览版本、最近检查时间、更新状态、目标版本、大小与更新说明。默认在首屏显示 1.5 秒后后台检查，可以关闭自动检查，也可以手动检查。网络或签名错误在该页显示，不弹窗阻挡启动。发现新版时，侧栏提供查看入口；没有自动下载安装的默认行为。

点击「下载并重启更新」后先确认。正在安装资源或运行游戏时停止更新，要求先完成 / 暂停任务或关闭游戏。下载可取消，取消删除未完成下载文件，已完整校验的暂存文件可在再次尝试时复用。

更新依次执行：验证发布者签名 → 下载并校验大小和 SHA-256 → 启动自身的独立临时副本 → 再次验签和校验 → 确认写权限 → 主程序退出 → 备份旧 EXE 并原子替换 → 重启 → 等待新窗口就绪确认。

新程序未能启动、提前退出或 40 秒内没有窗口确认时，尝试恢复旧 EXE 并重新打开旧版。替换失败沿用文件事务回滚。备份保留在程序目录 `.backup/fangyu-*`，结果在数据目录 `update/launcher-update-result.json`。更新只接受一个启动器 EXE，不删除其他文件；按本机现有文件名替换，因此中文名和用户自行改过的名称均可保留。私有游戏数据、账号和设置不在替换清单内。

临时更新器由当前单文件 EXE 复制而来，放在数据目录 `update/runners`，不依赖机器安装 .NET。完成后旧版备份和临时副本会保留，不做一键清空。副本入口不加载主窗口或主程序的单实例锁。

Windows 支持自动替换和重启；其他平台保留检查与发布页下载入口，尚未做自动替换实机验收。

## 更新源与版本

默认使用 `https://github.com/Insomnia-7/mc-launcher/releases/latest/download/update.json` 及相邻的 `update.json.sig`。原有环境变量 `MCLAUNCHER_UPDATE_URL` 和数据目录 `settings.json` 中 `updateUrl` 覆盖继续支持，仍必须通过内置公钥验签。

更新只比较版本，不按 Release 补录或发布时间判断；忽略 `+提交号` 构建元数据，保留 `preview.1` 等预发布标识。同版本的不同提交号不触发升级，补录 0.2～0.6 不触发降级，后续 Preview 版本可被识别。

## 后续发版必须同步的文件

每次桌面版发布都要同时上传：`Fangyu.exe`、`update.json`、`update.json.sig`，先建立草稿并核验附件，再公开设为 Latest。只推源码、只上传 EXE、只建立 Release 都不能完成自动更新发布。

本地生成清单与验签：

```powershell
pwsh -File mc-launcher/packaging/new-desktop-update.ps1 `
  -Executable '<成品目录>/方隅.exe' `
  -Version '0.7.0-preview.1' -Tag 'desktop-v0.7.0-preview.1' `
  -Notes '此版本的用户可读更新说明' `
  -PrivateKeyPath '<仓库外受保护备份>/fangyu-update-signing.dpapi'
```

新增 `Desktop Release` 手动工作流，选择已有的 `desktop-v*` 标签，执行核心 / 桌面回归、精简单文件打包、签名、验签和草稿发布。它使用现有 `release-signing` 环境里的 `MCLAUNCHER_UPDATE_SIGNING_KEY`；环境没有对应私钥时停止发布。旧的 `Release` 工作流面向 WPF 的 `v*` 标签，不负责新版桌面发布。

密钥沿用 RSA-PSS / SHA-256 和内置公钥验证。新本机私钥仅以 Windows 当前用户加密备份保存在仓库之外，公钥随源码发布；已有受信公钥继续保留，以兼容现有签名工作流。私钥不进入仓库、日志或 Release。
