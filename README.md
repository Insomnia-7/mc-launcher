# 方隅启动器

Minecraft Java 版桌面启动器，主界面基于 C# / .NET 10 / Avalonia，采用 [MIT License](LICENSE)。提供 Windows 单文件与 Linux / macOS 的 x64、ARM64 原生预览包；跨平台真实游戏组合仍在验收。

- 原版与 Fabric / Quilt / Forge / NeoForge 安装、自动准备 Java、微软登录、多账号切换与离线身份。
- Modrinth / CurseForge 模组、整合包、资源包、数据包及光影包检索与安装，支持分类、版本、来源、排序、热门推荐和分页。
- 多游戏版本分组；实例隔离、事务模组更新、诊断与独立更新器。
- 网络设置支持自动选择、直连及自定义 HTTP / HTTPS / SOCKS5 代理。
- 玩家中心：可旋转角色预览、正版皮肤与自有披风、本地离线外观、实例快照、设置预设、模组更新策略、可恢复任务与中文体检。
- 整合包升级工作区：预览文件差异、合并 JSON 设置、选择冲突处理并恢复更新前版本；启动前检查声明的模组依赖，下载队列支持自动接续与调整等待顺序。
- 游戏设置同步：按需共享按键、音量、画面、语言及服务器列表，保留实例例外并可恢复；统一页面切换、展开动画和完成反馈，支持关闭动效。
- Linux Secret Service / macOS 钥匙串账号加密、按系统与架构下载 Java、原生便携包及分平台签名更新，见 [跨平台预览说明](mc-launcher/docs/platform-preview.md)。

源代码和解决方案位于 **[mc-launcher/](mc-launcher/)**。

## 开始使用

用户下载发布包中的 `方隅.exe` 即可运行，无需另装 .NET。开发构建需要 .NET 10 SDK，在仓库根目录执行：

```powershell
dotnet run --project mc-launcher/src/Launcher.Desktop/Launcher.Desktop.csproj -c Release
```

本地生成单 EXE 开发包：

```powershell
pwsh -File mc-launcher/packaging/publish-desktop.ps1 -Lean -Compress
```

产物位于 `mc-launcher/dist/desktop-win-x64/方隅.exe`，包含运行时。玩家中心使用说明见 [玩家中心指南](mc-launcher/docs/player-center.md)，桌面实现和验收记录见 [Avalonia 桌面版](mc-launcher/docs/avalonia-desktop.md)。旧 WPF 入口与对应发布工具继续保留在源码中。

完整运行配置、资源来源权限说明、检查命令和开发记录见 **[启动器 README](mc-launcher/README.md)**。

本项目与 Mojang、Microsoft 没有关联，亦非官方产品。Minecraft 与第三方资源遵循各自的许可。
