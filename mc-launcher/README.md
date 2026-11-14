# 方隅 · 源码

Minecraft Java 版启动器。C# / .NET 10 / Avalonia 12，Windows 出单文件，Linux 和 macOS 出原生包。

界面在 `src/Launcher.Desktop`。旧的 WPF 入口 `src/Launcher.App` 还留着，能编译能跑，但只在 Windows 用，也不再出新功能。

```
src/
  Launcher.Domain          版本 JSON、classpath、启动参数、加载器规则，纯计算
  Launcher.Application     安装、启动编排、实例、模组、整合包、设置同步、更新
  Launcher.Infrastructure  HTTP、Mojang 元数据、Java、Modrinth、CurseForge、EasyTier
  Launcher.Desktop         Avalonia 界面
  Launcher.App             旧 WPF 界面（Windows only）
tests/
  Core.Checks              链路与业务检查，不依赖界面
  Desktop.Checks           Avalonia 无头渲染检查
  App.Checks               WPF 检查
  Platform.Checks          Unix 平台行为
  Architecture.Check       分层依赖约束
tools/
  Launcher.Cli             命令行链路验证（mcl）
  Launcher.Updater         自更新替换进程
  Launcher.Probe           探针
  Launcher.Bench           启动耗时测量
packaging/                 发布脚本、更新清单生成、签名
docs/                      各功能的设计与验收记录
```

## 跑起来

需要 .NET 10 SDK。

```powershell
dotnet run --project src/Launcher.Desktop/Launcher.Desktop.csproj -c Release
```

或者用现成脚本：Windows 是 `run-desktop.cmd`，Linux / macOS 是 `sh run-desktop.sh`。

## 出包

Windows，单文件自带运行时：

```powershell
pwsh -File packaging/publish-desktop.ps1 -Lean -Compress
```

脚本会断言输出目录里只有一个 exe，然后把 `方隅.exe` 拷到 `dist/desktop-win-x64/`。
`-Lean` 打开裁剪，压完约 24 MB；不加是完整版，体积翻一倍左右。

Linux / macOS：

```powershell
pwsh -File packaging/publish-unix.ps1 -Runtime linux-x64
```

## 跑检查

```powershell
./tests/Core.Checks/bin/Release/net10.0/Core.Checks.exe
./tests/Desktop.Checks/bin/Release/net10.0/Desktop.Checks.exe <空目录>
./tests/Desktop.Checks/bin/Release/net10.0/Desktop.Checks.exe <空目录> --multiplayer
```

先构建：

```powershell
dotnet build tests/Desktop.Checks/Desktop.Checks.csproj -c Release
```

`Desktop.Checks` 是无头渲染，要用一个空的目录当数据根，不同用例别复用同一个目录，否则会读到上一轮留下的实例。
`--empty`、`--workspace`、`--mod-workbench`、`--multiplayer` 这类开关只跑对应的那一组。

## 数据目录

默认 `%APPDATA%\MCLauncher`。开发时常用环境变量隔离：

```
MCLAUNCHER_HOME       数据目录（账号、实例、缓存、日志）
MCLAUNCHER_GAME_ROOT  游戏目录（versions、libraries、assets、instances）
```

## 发布

版本号在 `src/Launcher.Desktop/Launcher.Desktop.csproj`。改完版本号要一起更新：

1. `docs/releases/history.json` 追加一条
2. `CHANGELOG.md` 顶部加一节
3. 构建 exe，跑 `packaging/new-desktop-update.ps1` 生成并签名 `update.json`
4. Release 里必须同时上传 `Fangyu.exe`、`update.json`、`update.json.sig`，并设为 latest

更新清单的签名用 `scripts/update-signing.ps1`，私钥通过 `-PrivateKeyPath` 指定（不放进仓库）。清单里的下载地址、文件大小和 SHA-256 对不上时，客户端会拒绝这次更新。

## 几个约定

- `versions/<id>/<id>.json` 是版本的事实来源，Forge / NeoForge 的增量文件必须沿 `inheritsFrom` 合并之后才能拿来启动。
- 实例和版本是两回事：一个版本目录可以被多个实例共用（比如同一份 Fabric 版本装两个整合包），实例名会带 `-2` 后缀。
- 删实例时写墓碑（`instances/hidden-versions.json`、`hidden-instances.json`），避免被依赖保留的父版本或半删目录在重新扫描时又冒出来。重装同版本会自动清掉。
- 游戏进程启动优先走命令行直传（宽字符，中文路径安全）；参数超长才落到 `@argfile`，而 argfile 在 Windows 上要按系统 ANSI 代码页写，写 UTF-8 会让中文 classpath 条目失效。
- 界面动画统一走 `MotionPreferences`，用户在设置里关掉动效后，页面切换、指示条、波纹都不播。

## 文档

| 内容 | 文件 |
|---|---|
| 跨平台范围与依赖 | `docs/platform-preview.md` |
| 联机与 EasyTier | `docs/multiplayer.md` |
| 模组工作台 | `docs/mod-workbench.md` |
| 整合包升级 | `docs/modpack-upgrades.md` |
| 存档、修复与存储 | `docs/worlds-repair-storage.md` |
| 设置同步与动效 | `docs/settings-sync-and-motion.md` |
| 自动更新与签名 | `docs/launcher-auto-update.md`、`docs/update-signing.md` |
| 发布约定 | `docs/release-policy.md` |
| 界面规范 | `docs/ui-reference.md`、`docs/appearance.md` |

## 许可

MIT。Minecraft 与第三方资源遵循各自的许可，本项目与 Mojang、Microsoft 无关联。
