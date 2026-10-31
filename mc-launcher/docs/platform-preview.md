# Linux / macOS 适配预览 · 1.0

第三批覆盖系统凭据、Java 自动管理、原生便携包、按平台分发的签名更新和对应系统测试。Windows 继续交付单 EXE；本版仍为预览，不将持续集成的结果当作全部发行版和真实 Minecraft 的验收。

## 凭据

Windows 继续使用 DPAPI。macOS 通过 Security Framework 的 SecItem API 使用登录钥匙串；Linux 通过 `secret-tool` 使用桌面 Secret Service（GNOME Keyring 或兼容服务）。系统仅保存随机加密密钥，账号文件使用 AES-GCM 密文；密钥环不可用或拒绝访问时不回退明文。

Linux 需要桌面 D-Bus 会话、已解锁的密钥环与 `libsecret-tools`。没有这些服务时离线身份可用，持久化微软登录不可用。macOS 第一次访问可能请求系统授权；移动或更换应用后系统可能再次请求。默认数据目录沿用 .NET 的 ApplicationData/MCLauncher 约定，已有 Windows 数据目录不迁移；环境变量 `MCLAUNCHER_HOME` 与 `MCLAUNCHER_GAME_ROOT` 继续有效。

## Java 与运行

按本机系统和启动器进程架构选择 Temurin JRE：Windows ZIP，Linux / macOS tar.gz；支持 x64 与 ARM64。下载后校验 SHA-256，暂存解压、保留 Unix 执行权限，实际执行 `-version` 后才替换安装目录。macOS 的 `Contents/Home` 布局会归一到启动器管理的 Java 目录。已有可用 Java 优先复用，不全盘扫描；macOS 启动计划补充 `-XstartOnFirstThread`。

旧 Minecraft、加载器或模组可能没有 ARM64 原生库；启动器本机运行成功不代表这些游戏组合均兼容。

## 安装包与更新

- Windows x64：`Fangyu.exe` / ZIP，沿用原单文件分发。
- Linux x64 / ARM64：`Fangyu-<版本>-linux-<架构>.tar.gz`，解压后执行 `./Fangyu`。桌面依赖包括 X11、ICE、SM、fontconfig、glib 和中文字体。随包的 desktop 文件可由发行版集成工具安装；便携运行无需注册系统菜单。
- macOS Intel / Apple Silicon：对应架构 ZIP，内含「方隅.app」，拖到可写的应用目录再打开。构建提供 ad-hoc 完整性签名；没有 Apple Developer ID 签名或公证，系统首次运行可能要求在系统设置中允许。没有自动关闭 Gatekeeper 的脚本。

构建命令：`pwsh -File packaging/publish-unix.ps1 -Runtime linux-x64`；其他选项为 `linux-arm64`、`osx-x64`、`osx-arm64`。macOS 包必须在 macOS 上生成图标、签名与归档。

Windows 更新继续使用 `update.json`；Unix 使用 `update-<RID>.json` 及独立签名。清单声明系统与架构，不匹配时拒绝。Linux 更新单个可执行文件；macOS 更新可执行文件、Info.plist、图标与签名资源，避免只替换二进制破坏应用包签名。Unix 备份放在启动器更新数据目录，不放进 .app。更新前验证所有文件，保留执行位；重启未就绪时恢复旧文件。只读目录、磁盘映像和开发运行不支持自替换，请改用发布包或可写目录。

维护者必须同时发布对应平台的清单、签名与全部载荷。首次安装 1.0 的 Linux/macOS 用户之后才具备这些更新能力；Windows 0.7+ 用户继续使用原更新源。

## 验证

`tests/Platform.Checks` 验证凭据密文、篡改拒绝、六种 Java 包布局、平台与架构校验，以及全文件更新、备份和恢复。`--native-credentials --live-java` 在隔离目录使用真实系统密钥环，并下载、校验、解压及执行官方 Java。

`Platform Preview` 工作流在 Linux x64、Linux ARM64、macOS Intel 和 macOS Apple Silicon 上分别构建便携包并打开真实 Avalonia 窗口。测试账号内容为隔离夹具，不访问个人账号；原生窗口启动、Java 与系统密钥环结果随构建产物保存。真实微软授权、Minecraft 图形运行、全部模组组合和各 Linux 桌面环境仍需分别验收。

参考：[Avalonia 平台与依赖](https://docs.avaloniaui.net/docs/supported-platforms)、[.NET macOS 分发](https://learn.microsoft.com/en-us/dotnet/core/deploying/macos)、[Apple Keychain](https://developer.apple.com/documentation/security/keychain-services)、[libsecret 实现](https://github.com/GNOME/libsecret/blob/main/tool/secret-tool.c)。
