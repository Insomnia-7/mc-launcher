# 方隅启动器

Minecraft Java 版桌面启动器，主界面基于 C# / .NET 10 / Avalonia，采用 [MIT License](LICENSE)。当前提供 Windows 单文件包；核心与界面保留 Linux / macOS 适配基础，尚未完成这两个平台的发行验收。

- 原版与 Fabric / Quilt / Forge / NeoForge 安装、自动准备 Java、微软登录、多账号切换与离线身份。
- Modrinth / CurseForge 模组、整合包、资源包、数据包及光影包检索与安装，支持分类、版本、来源、排序、热门推荐和分页。
- 多游戏版本分组；实例隔离、事务模组更新、诊断与独立更新器。
- 网络设置支持自动选择、直连及自定义 HTTP / HTTPS / SOCKS5 代理。
- 玩家中心：可旋转角色预览、正版皮肤与自有披风、本地离线外观、实例快照、设置预设、模组更新策略、可恢复任务与中文体检。

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
