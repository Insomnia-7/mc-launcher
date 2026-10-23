# MC Launcher

Windows Minecraft Java 版启动器，使用 C# / .NET 10 / WPF。

## 当前能力

- 原版安装与启动，自动准备 Java。
- Fabric、Quilt、Forge、NeoForge；Modrinth / CurseForge / MultiMC·Prism 整合包导入。
- Modrinth 与 CurseForge 资源获取、模组前置补全、实例隔离和独立设置。
- 微软设备码登录、离线身份、DPAPI 令牌存储、本地诊断及独立更新器。
- **首次安装向导**：选择正式版、内存与身份，一键准备游戏和 Java，可选择完成后启动。

首次进入空游戏目录会显示安装向导。已有版本时不会自动弹出，可点击左下角「安装向导…」打开。
「稍后设置」返回主界面，可继续使用下载中心和整合包导入。安装失败或取消后可重试，下载器复用已校验的文件；仅当游戏与 Java 都准备好后保存本次实例内存设置。

## 本机运行

在当前开发机上，双击 `run-app.cmd`。脚本使用：

- .NET：`<dotnet-sdk>`
- 启动器数据：`%APPDATA%\MCLauncher`
- 游戏目录：`<game-root>`

已有游戏时，点击左下角「安装向导…」体验新流程。

## 构建与检查

需要 .NET 10 SDK 和 Windows 桌面运行时。

```powershell
dotnet build MinecraftLauncher.sln -c Release -m:1
dotnet tests/Core.Checks/bin/Release/net10.0/Core.Checks.dll
dotnet tests/Architecture.Check/bin/Release/net10.0/Architecture.Check.dll
```

核心检查现为 311 项。首次安装相关检查在 `tests/Core.Checks/FirstRunChecks.cs`。
如默认 `dotnet` 版本较旧，本机可使用 `<dotnet-sdk>\dotnet.exe`。

## 路线与交接

本轮接手、验证范围及后续优先事项见 [开发交接](docs/handoff-2026-10-23-codex.md)。
多账号切换界面、更新清单签名和许可证选择仍待处理。
