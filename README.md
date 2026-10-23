# MC Launcher

Minecraft Java 版 Windows 桌面启动器，基于 C# / .NET 10 / WPF，采用 [MIT License](LICENSE)。

- 原版与 Fabric / Quilt / Forge / NeoForge 安装、自动准备 Java、微软登录、多账号切换与离线身份。
- Modrinth / CurseForge 模组、整合包、资源包、数据包及光影包检索与安装，支持分类、版本、来源、排序、热门推荐和分页。
- 正式版、预览版、活动版与远古版本分组；实例隔离、事务模组更新、诊断与独立更新器。

源代码和解决方案位于 **[mc-launcher/](mc-launcher/)**。

## 开始使用

需要 Windows 和 .NET 10 SDK，在仓库根目录执行：

```powershell
dotnet build mc-launcher/MinecraftLauncher.sln -c Release -m:1
dotnet mc-launcher/src/Launcher.App/bin/Release/net10.0-windows/MCLauncher.dll
```

完整运行配置、资源来源权限说明、检查命令和开发记录见 **[启动器 README](mc-launcher/README.md)**。

本项目与 Mojang、Microsoft 没有关联，亦非官方产品。Minecraft 与第三方资源遵循各自的许可。
