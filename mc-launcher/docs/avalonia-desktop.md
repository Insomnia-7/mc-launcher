# Avalonia 桌面界面：第一阶段

新版入口是 `src/Launcher.Desktop`，使用 Avalonia 12.1.4 和 .NET 10，目标为 Windows、Linux、macOS。旧版 WPF 入口保留。现阶段已交付可运行的首页与共用业务连接，不代表三个操作系统的全部功能均已验收。

## 运行与构建

Windows：运行 `run-desktop.cmd`，沿用本机忽略的 `launcher.local.cmd`。已有构建时直接运行，不会每次重编译。

Linux / macOS：安装 .NET 10 SDK 后执行 `sh run-desktop.sh`。可使用 `MCLAUNCHER_HOME` 和 `MCLAUNCHER_GAME_ROOT` 指定数据、游戏目录。需要图形桌面和中文字体。Linux 图形系统及 macOS 版本要求以所选 Avalonia 版本的支持清单为准。

跨平台解决方案：

```sh
dotnet build MinecraftLauncher.Desktop.slnx -c Release
dotnet run --project tests/Desktop.Checks -c Release -- /path/to/isolated-test-output
```

旧版仍使用 `MinecraftLauncher.sln`、`run-app.cmd`。

## Windows 单文件分发

发布配置为 `Properties/PublishProfiles/WindowsSingleFile.pubxml`。在应用目录执行：

```powershell
pwsh -NoProfile -File packaging/publish-desktop.ps1
```

生成 `dist/desktop-win-x64/MCLauncher.exe`，用户只需获取这一个文件，双击运行。它包含 .NET 10 运行环境和原生绘图库，无需预装 .NET，也不依赖源码目录。当前产物面向 Windows x64，Linux / macOS 需要分别发布对应平台的安装包。

脚本支持 `-Dotnet` 指定 SDK 路径、`-OutputDirectory` 指定交付目录、`-RestoreSources` 指定 NuGet 源。默认关闭包内压缩以优先保证启动速度；可用 `-Compress` 生成较小下载包。保持关闭裁剪，避免破坏反射绑定与共享业务。调试符号不进入分发目录。

单文件指下载产物；运行时仍会创建正常的用户设置、游戏数据和原生库缓存。游戏本体及所需 Java 不包含在这个 EXE 中，Windows x64 延续原有自动准备逻辑。新版暂不接收旧版 WPF 自动更新包。

本次未压缩产物为 103,448,974 字节（98.7 MiB）。已把最终重命名的 EXE 单独复制到隔离目录，把 `DOTNET_ROOT` 指向不存在的目录，验证首次解包、再次启动和空游戏库首次运行均出现可响应的原生窗口。宿主诊断确认 self-contained，绘图库来自自己的包缓存；分发目录启动后仍只有 EXE。这不替代干净虚拟机的正式发布验收或真实游戏启动验收。

打包原理参见 [.NET 官方单文件部署文档](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/)。

## 已完成

- 版本、账号和启动按钮归入主卡片，导入 / 备份为次级操作。
- 版本列表增加方块图标，保留版本与加载器文字；说明收进可展开区域。
- 真实版本目录、账号切换、设置保存、启动准备、资源页与安装向导复用原业务代码。
- 六套主题、壁纸导入 / 导出、网络设置和确认对话框。
- 按钮缩放回弹、页面淡入位移、弹窗进出、昼夜主题开关及星点分时出现。
- 页面和弹窗的过渡可取消；动画只在状态变化时运行，没有常驻动画循环。
- 新增 `PlatformUi` 适配点，分别使用 WPF / Avalonia 的文件选择、剪贴板、图像和调度接口。

## 跨平台验收边界

当前开发机为 Windows。Linux 和 macOS 使用同一套 Avalonia 界面代码，但尚未完成实机验收。

- Java 检测支持 `JAVA_HOME` / `PATH`；共用 Java 自动下载仍只支持 Windows x64。非 Windows 预览版要求用户先安装合适的 Java，不会下载 Windows 安装包。Windows ARM64 的自动 JRE 下载也需要后续适配。
- Windows 继续使用 DPAPI 保存微软账号。Linux / macOS 的系统密钥环尚未接入，微软账号持久化登录暂不开放，离线身份可用；新版不会退回明文保存登录令牌。
- 新前端暂不检查或应用旧版 WPF 更新包，需要为新版建立独立发布渠道。
- 尚未验收 Linux / macOS 的游戏本体启动、原生依赖、文件权限、应用签名及打包分发。
- HarmonyOS 后续单独评估。
- 当前玻璃质感使用半透明面板、边缘高光和背景配色，未实现逐像素的实时光学折射。

## 验证

`Desktop.Checks` 在隔离目录创建游戏元数据，渲染真实 Avalonia 控件，并通过模拟鼠标输入验证账号入口、启动准备、设置保存、主题切换、快速切页、按钮按压恢复、关闭动画和最小窗口布局。启动检查使用模拟 Java 与进程适配器，不会下载或启动真实 Minecraft。

性能比较需要分别记录进程启动至窗口就绪、空闲内存与交互帧耗时。无窗口测试中的控件构造时间不能等同于真实冷启动耗时。
