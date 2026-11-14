# 方隅

Minecraft Java 版启动器，Windows 桌面程序。用 C#、.NET 10 和 Avalonia 写成，[MIT 许可](LICENSE)。

下载 Release 里的 `方隅.exe`，双击就能开，不用装 .NET。想改名成 `Fangyu.exe` 也可以。

**当前版本**：[1.0.0-preview.6](https://github.com/Insomnia-7/mc-launcher/releases/latest)（Windows x64 单文件）

## 能做什么

**装游戏。** 原版，以及 Fabric、Quilt、Forge、NeoForge 四种加载器。本机没有合适的 Java 会自动下一份（Adoptium 的 Temurin），校验完才解压。

**分版本。** 每个游戏版本单独一个实例，可以改显示名、分组、收藏和封面。删版本会连它自己的 mods、config、存档一起删，确认框里写清楚了；被别的加载器版本依赖的那份文件会留下，不会顺手删掉。

**找东西。** 接 Modrinth 和 CurseForge，模组、整合包、资源包、数据包、光影包都能搜，按游戏版本、加载器、分类筛选，按热度或更新时间排。中文关键词会映射到原项目名。装好的模组能多选批量启用、停用、删除；检查更新时先列出改哪些文件、多大、有没有缺前置，看完再决定。想试又怕搞坏，可以建一个试装副本，存档默认不复制。

**存档。** 世界列表带图标、版本、模式、最近游玩时间和大小。备份、恢复都只动那一个世界，恢复前自动存一份当前的。

**同步。** 挑一个实例当来源，把按键、音量、画面、语言或服务器列表同步给别的实例。默认关闭，先看差异再应用，冲突时保留本地。

**联机。** 局域网里能扫到别人开放的世界，也能建房走 EasyTier 组私网。组件按需下载，不装虚拟网卡、不改路由。跨网络还需要一个可用的公共或自有节点。

**更新。** 0.7 之后可以在设置里检查更新，下载后校验签名和 SHA-256，替换、重启，新窗口起不来会退回旧版。

## 界面

只有一套浅色主题。左侧导航可以收起来（236px ↔ 72px），收起后只剩图标，鼠标停上去显示名称。切页时那条蓝色指示条会平滑移过去。按钮按下去有水波纹和轻微缩放。

## 平台

| 平台 | 状态 |
|---|---|
| Windows x64 | 1.0.0-preview.6，单文件自带运行时 |
| Linux x64 / ARM64 | 1.0.0-preview.1 预览包，需要系统 X11、字体和桌面密钥环 |
| macOS x64 / ARM64 | 1.0.0-preview.1 预览包，ad-hoc 签名，未做 Apple 公证 |

Linux 和 macOS 包还没跟着 Windows 一起更新。macOS 首次打开可能要在系统设置里放行。真实微软授权和 Minecraft 图形运行在非 Windows 平台上仍待验收。

## 从源码跑

需要 .NET 10 SDK。

```powershell
dotnet run --project mc-launcher/src/Launcher.Desktop/Launcher.Desktop.csproj -c Release
```

打一个自带运行时的单文件：

```powershell
pwsh -File mc-launcher/packaging/publish-desktop.ps1 -Lean -Compress
```

产物在 `mc-launcher/dist/desktop-win-x64/方隅.exe`。

源码结构、检查命令和发版流程都在 **[mc-launcher/README.md](mc-launcher/README.md)**。

## 数据放在哪

默认在 `%APPDATA%\MCLauncher`，游戏文件在它下面的 `game`。想换位置就设环境变量：

- `MCLAUNCHER_HOME`：数据目录
- `MCLAUNCHER_GAME_ROOT`：游戏目录

## 说明

个人项目，跟 Mojang、Microsoft 没有任何关系，也不是官方产品。Minecraft 本体、模组和其他资源各归各自的许可。

有问题或者想法，直接开 issue。
