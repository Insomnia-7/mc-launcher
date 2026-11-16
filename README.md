# 方隅 Fangyu

[![Release](https://img.shields.io/github/v/release/Insomnia-7/mc-launcher?style=flat-square&include_prereleases&label=release)](https://github.com/Insomnia-7/mc-launcher/releases)
[![Downloads](https://img.shields.io/github/downloads/Insomnia-7/mc-launcher/total?style=flat-square)](https://github.com/Insomnia-7/mc-launcher/releases)
[![License](https://img.shields.io/badge/license-MIT-blue?style=flat-square)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%20x64-lightgrey?style=flat-square)](#平台支持)

一款 Minecraft Java 版启动器，可选接入 Horizon Rust 客户端，用 C# 与 Avalonia 写成，Windows 上以单文件分发。

**当前版本**：1.0.0-preview.20（预览版）

## 简介

方隅是一个个人维护的 Minecraft 启动器项目。它把原版与常见模组加载器的安装、版本与实例管理、资源下载、存档备份这些日常操作收拢到一个界面里，尽量少让你在文件夹和网页之间来回切。

技术上它没有为了跨平台而牺牲 Windows 上的体验：界面用 Avalonia 写，逻辑层是纯 .NET，所以同一套代码可以出 Windows 单文件，也能出 Linux 和 macOS 的原生包。启动参数、classpath 拼装、版本继承合并这些容易出错的地方都放在可单测的领域层里，界面上做的事情尽量只是展示结果。

项目目前处于预览阶段。Windows 版本持续更新，Linux 与 macOS 的包还停在早期版本，真实游戏运行仍需要在对应平台上做验收。

更详细的介绍见[项目简介](mc-launcher/docs/intro.zh-Hans.md)。

## 功能特性

* **首页**：直接选择并启动实例，查看真实联机与网络状态，使用紧凑实例列表和六个常用入口；完整游戏库仍可单独管理。
* **版本安装**：原版，以及 Forge、NeoForge、Fabric、Quilt 四种加载器。版本文件按 `inheritsFrom` 合并后再使用，Forge / NeoForge 的增量配置不会漏。
* **Java 管理**：本机没有合适的 Java 时自动下载对应大版本的 JRE，校验哈希后解压安装；也可以手动指定。
* **实例管理**：一个版本目录可以被多个实例共用，实例可以改显示名、分组、收藏、封面，支持卡片与列表两种布局。
* **资源下载**：接入 Modrinth 与 CurseForge，支持模组、整合包、资源包、数据包、光影包；按游戏版本、加载器、分类筛选，按热度或更新时间排序，中文关键词会映射到原项目名。
* **模组工作台**：多选批量启用、停用、删除；更新前先预览会改动哪些文件、下载多大、是否缺前置，再决定是否备份应用。
* **存档与维护**：世界列表带图标、版本、模式、最近游玩时间与大小，支持单世界备份与恢复；诊断可只补下缺失或校验失败的依赖；存储页可清理过期缓存。
* **设置同步**：选择来源实例，把按键、音量、画面、语言或服务器列表同步给其他实例，默认关闭，先看差异再应用。
* **联机**：扫描局域网内开放的世界并直接连接；也可以创建 EasyTier 私密房间跨网络组队，组件按需下载，不安装虚拟网卡、不修改路由。
* **账号**：微软正版登录、多账号切换、离线身份；Linux 使用 Secret Service、macOS 使用钥匙串保存凭据。
* **自动更新**：下载后校验发布者签名与 SHA-256 再替换，新进程起不来会回退到旧版本。
* **界面**：深色、浅色与跟随系统，侧栏四态（展开 / 收起 / 极窄 / 浮层），切换页面时导航指示条平滑跟随，按钮按下 85ms 轻微收回、松开 140ms 复位；支持异步加载反馈、弹窗动画与四态通知队列，动效可在设置里关闭。

> 注意：Linux 与 macOS 包目前停留在预览阶段，跨平台能力仍在验收，详见[跨平台说明](mc-launcher/docs/platform-preview.md)。

### 技术栈

[![.NET](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Avalonia](https://img.shields.io/badge/Avalonia-12.1-8B44AC?style=flat-square)](https://avaloniaui.net/)
[![C#](https://img.shields.io/badge/C%23-13-239120?style=flat-square&logo=csharp&logoColor=white)](https://learn.microsoft.com/dotnet/csharp/)

## 开始使用

前往 [Releases](https://github.com/Insomnia-7/mc-launcher/releases) 页面，在最新的 Pre-release 里下载 `Fangyu.exe`，双击即可运行，不需要另装 .NET。下载后可以随意改名为 `方隅.exe`。

首次启动会直接进入游戏库。点右上角「添加游戏」，选一个版本、确认内存和身份，就会开始下载安装。

### 平台支持

| 平台 | 系统版本 | 架构 | 分发形式 |
|---|---|---|---|
| Windows | 10 及以上 | `x86_64` | 单文件 `.exe`，自带运行时 |
| Linux | 需要 X11、fontconfig、glib 及中文字体 | `x86_64`, `aarch64` | `.tar.gz` 原生包（预览） |
| macOS | 15 及以上 | `x86_64`, `aarch64` | `.zip` 应用包，ad-hoc 签名（预览） |

Linux 与 macOS 的包由系统密钥环保存凭据，需要对应的桌面服务处于可用状态；macOS 首次打开可能需要在系统设置中允许。

<details>
<summary><b>从命令行安装</b></summary>

#### Windows

用 PowerShell 直接取最新版本的程序，无需打开浏览器：

```powershell
$r = Invoke-RestMethod 'https://api.github.com/repos/Insomnia-7/mc-launcher/releases?per_page=10'
$latest = $r | Where-Object { -not $_.draft } | Sort-Object published_at -Descending | Select-Object -First 1
$asset = $latest.assets | Where-Object { $_.name -eq 'Fangyu.exe' }
Invoke-WebRequest $asset.browser_download_url -OutFile "$env:USERPROFILE\Downloads\方隅.exe"
```

下载后核对哈希，再运行：

```powershell
Get-FileHash "$env:USERPROFILE\Downloads\方隅.exe" -Algorithm SHA256
& "$env:USERPROFILE\Downloads\方隅.exe"
```

`update.json` 里记录了对应版本的文件大小与 SHA-256，想更严谨可以拿它比对。

#### 从源码构建

需要 .NET 10 SDK：

```powershell
git clone https://github.com/Insomnia-7/mc-launcher.git
cd mc-launcher
dotnet run --project mc-launcher/src/Launcher.Desktop/Launcher.Desktop.csproj -c Release
```

打出单文件：

```powershell
pwsh -File mc-launcher/packaging/publish-desktop.ps1 -Lean -Compress
```

产物在 `mc-launcher/dist/desktop-win-x64/方隅.exe`。

#### Linux / macOS

```bash
git clone https://github.com/Insomnia-7/mc-launcher.git
cd mc-launcher/mc-launcher
sh run-desktop.sh
```

</details>

## 开发与贡献

项目用 .NET 10 + Avalonia 12，分层为 Domain（纯计算）、Application（业务编排）、Infrastructure（HTTP、各平台接口）、Desktop（界面）。目录职责与构建细节见[源码说明](mc-launcher/README.md)。

跑一遍检查：

```powershell
dotnet build tests/Desktop.Checks/Desktop.Checks.csproj -c Release
./tests/Core.Checks/bin/Release/net10.0/Core.Checks.exe
./tests/Desktop.Checks/bin/Release/net10.0/Desktop.Checks.exe <空目录>
```

欢迎提 Issue 说明问题和复现步骤，也欢迎直接开 Pull Request。改动涉及启动参数、版本合并、删除逻辑时，请一并补上对应的检查项。

## 贡献者

感谢每一位提交过代码、报告过问题的人。

[![Contributors](https://contrib.rocks/image?repo=Insomnia-7/mc-launcher)](https://github.com/Insomnia-7/mc-launcher/graphs/contributors)

## 版权声明

版权所有 © 2026 方隅项目作者。

> 本软件并非官方 Minecraft 服务，未获得 Mojang 或 Microsoft 的批准或关联许可。

本项目基于 [MIT 许可证](LICENSE) 发布。Minecraft 本体、模组、整合包及其他第三方资源遵循各自的许可协议，本项目不重新分发这些内容。
