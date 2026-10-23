# MC Launcher

Minecraft Java 版 Windows 桌面启动器，使用 C#、.NET 10 和 WPF。

## 功能

- 原版安装与启动、自动准备 Java、官方 / BMCLAPI 下载源切换。
- 游戏版本分为正式版、预览版、活动版、远古 Beta、远古 Alpha；活动版收录官方清单中可下载的特殊版本（如愚人节版）。
- Fabric、Quilt、Forge、NeoForge；Modrinth / CurseForge / MultiMC·Prism 整合包导入。
- 模组、整合包、资源包、数据包、光影包统一检索：来源、游戏版本、内容分类、加载器、热门推荐及分页。
- 按相关度、下载量、热度、更新时间和发布时间排序。Modrinth 热度按关注数，CurseForge 按平台人气；排序在平台端完成。
- 实例隔离、独立内存与启动参数；删除实例时回收无依赖的版本与 natives，保留共享 libraries、assets 及仍被使用的版本。
- 模组依赖补全、来源记录与事务更新；同一游戏目录的写任务互斥。
- 微软设备码登录、多账号切换与移除、离线身份、DPAPI 令牌存储、本地诊断及独立更新器。
- 首次安装向导：选择正式版、内存与身份，一键准备游戏和 Java。

## 使用

首次进入空游戏目录会显示安装向导。已有版本时，可点击左下角「安装向导…」。
在下载页选择资源类型和来源，按需输入名称、选择游戏版本与分类，再搜索。留空搜索或点击「热门推荐」可浏览热门内容。改变条件后重新搜索，分页保留这次搜索的条件。

整合包安装会创建独立实例，并遵循搜索时选定的游戏版本、加载器。其他资源始终安装适配当前目标实例的发布文件；搜索到其他游戏版本的内容时，须先选择相应实例；筛选版本或加载器与目标实例不一致时会明确提示并停止安装。
数据包下载到实例的 `.minecraft/datapacks`，使用时需自行移入目标世界的 `saves/<世界>/datapacks`。

Modrinth 不需要 API Key。CurseForge 需要有效 Key，可通过 `CURSEFORGE_API_KEY` 或启动器数据目录 `settings.json` 的 `curseForgeApiKey` 提供。若 Key 无搜索权限，页面会明确提示，并保留按数字项目 ID 查询安装；此时无法获取该来源的热门列表。普通项目网页的 slug 链接不等于项目数字 ID。

## 账号管理

在版本页点击「账号管理…」，选择已保存的正版账号或离线身份，再点击「使用此身份」。切换已保存账号不需要重新完成设备码登录，选择会保存供下次启动使用。
「添加微软账号」复用微软设备码授权，支持取消；再次登录同一账号会更新登录信息。移除账号需要确认，仅清理本机登录信息；移除当前正版账号后回到离线身份，其他账号和游戏存档保留。首次安装向导也可选择或添加账号。

仅在启动时验证当前账号，不批量续期、不增加启动时网络请求。正版账号验证失败时保留原选择并停止这次启动；可重新登录，或主动选择离线身份。账号保存失败时不会宣称切换或移除成功。
账号令牌继续由 Windows DPAPI 保护；账号列表仅展示名称和状态。详细实现与验证见 [账号管理记录](docs/account-management.md)。

## 构建与运行

需要 Windows 和 .NET 10 SDK（含桌面运行时）。在本目录执行：

```powershell
dotnet build MinecraftLauncher.sln -c Release -m:1
dotnet src/Launcher.App/bin/Release/net10.0-windows/MCLauncher.dll
```

默认数据目录为 `%APPDATA%/MCLauncher`，游戏目录为其 `game` 子目录。可通过 `MCLAUNCHER_HOME`、`MCLAUNCHER_GAME_ROOT` 覆盖。
可双击 `run-app.cmd` 启动已构建的程序，或通过 `run.cmd` 在首次运行时构建。脚本优先使用 `DOTNET_ROOT`，否则使用 PATH 中的 .NET SDK。
本机配置可写入被 Git 忽略的 `launcher.local.cmd`，用 `set "变量=值"` 指定上述环境变量；该文件不会随源码发布。

## 检查

```powershell
dotnet tests/Core.Checks/bin/Release/net10.0/Core.Checks.dll
dotnet tests/Architecture.Check/bin/Release/net10.0/Architecture.Check.dll
dotnet tests/App.Checks/bin/Release/net10.0-windows/App.Checks.dll
```

核心检查涵盖安装、登录、依赖、事务更新、资源 API 参数、版本分类与实例删除；WPF 检查涵盖安装向导、检索分页、取消、分类切换、权限错误以及 1000×640 / 820×560 布局渲染。测试使用隔离目录及模拟 API，不下载完整游戏。

## 架构与开发记录

Domain / Application 核心不依赖 WPF；Infrastructure 实现网络和系统接口，App 提供桌面界面，独立工具负责诊断及更新。

- [资源检索与清理交接](docs/resource-browser-2026-10-23.md)
- [接手交接](docs/handoff-2026-10-23-codex.md)
- [代码审查](docs/review-and-optimization-2026-10-23.md)
- [六项后续修复](docs/followup-fixes-2026-10-23.md)

更新清单签名和发布密钥管理仍待后续开发。

## 许可证

本仓库原创代码采用 [MIT License](../LICENSE)。第三方库、下载的 Minecraft 文件与社区资源仍适用各自的许可和使用条件。
本项目与 Mojang、Microsoft 没有关联，亦非官方产品。
