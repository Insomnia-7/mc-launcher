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

推荐精简配置为 `Properties/PublishProfiles/WindowsLean.pubxml`，基线配置为 `WindowsSingleFile.pubxml`。在应用目录执行：

```powershell
pwsh -NoProfile -File packaging/publish-desktop.ps1 -Lean -Compress
```

生成 `dist/desktop-win-x64/MCLauncher.exe`，用户只需获取这一个文件，双击运行。它包含 .NET 10 运行环境和原生绘图库，无需预装 .NET，也不依赖源码目录。当前产物面向 Windows x64，Linux / macOS 需要分别发布对应平台的安装包。

脚本支持 `-Dotnet` 指定 SDK 路径、`-OutputDirectory` 指定交付目录、`-RestoreSources` 指定 NuGet 源。`-Lean` 启用保守依赖裁剪，`-Compress` 压缩单文件中的程序集；不传这两个开关可发布完整、未压缩的基线包。调试符号不进入分发目录。

单文件指下载产物；运行时仍会创建正常的用户设置、游戏数据和原生库缓存。游戏本体及所需 Java 不包含在这个 EXE 中，Windows x64 延续原有自动准备逻辑。新版暂不接收旧版 WPF 自动更新包。

本次黑白主题精简压缩产物为 22,749,964 字节（21.7 MiB），此前完整框架未压缩包为 98.7 MiB、仅压缩包为 47.1 MiB。已把最终重命名的 EXE 单独复制到隔离目录，把 `DOTNET_ROOT` 指向不存在的目录，验证首次解包、再次启动和空游戏库首次运行均出现可响应的原生窗口。宿主诊断确认 self-contained，绘图库来自自己的包缓存；分发目录启动后仍只有 EXE。这不替代干净虚拟机的正式发布验收或真实游戏启动验收。

打包原理参见 [.NET 官方单文件部署文档](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/)。

## 已完成

- 版本、账号和启动按钮归入主卡片，导入 / 备份为次级操作。
- 版本列表增加方块图标，保留版本与加载器文字；说明收进可展开区域。
- 真实版本目录、账号切换、设置保存、启动准备、资源页与安装向导复用原业务代码。
- 仅保留日间白、夜间黑两套灰阶主题，统一主按钮、选中态、插画和原生控件强调色；昼夜按钮双向切换，主按钮字色跟随反转。
- 已移除其他配色及配色导入 / 导出入口；旧彩色 / 自定义主题迁移为夜间黑，保留壁纸强度和动画偏好。仍支持本地壁纸、网络设置和确认对话框。
- 按钮缩放回弹、页面淡入位移、弹窗进出、昼夜主题开关及星点分时出现。
- 页面和弹窗的过渡可取消；动画只在状态变化时运行，没有常驻动画循环。
- 新增 `PlatformUi` 适配点，分别使用 WPF / Avalonia 的文件选择、剪贴板、图像和调度接口。

## 精简方式与验证边界

本轮发布版本为 `0.2.0-preview.1`，GitHub 标签使用 `desktop-v0.2.0-preview.1`。`desktop-` 前缀用于区分现有 `v*` 标签触发的 WPF 发布工作流；此预发布版只上传新版单文件 EXE 和预览图，不生成旧渠道的更新清单，不设为 Latest。

- 界面使用带明确类型的编译绑定，包括版本、账号、资源列表和模组模板，避免运行时查找属性。
- `TrimMode=partial` 仅裁剪已声明可裁剪的依赖。四个启动器程序集显式列为 `TrimmerRootAssembly`，完整保留业务模型、JSON 构造函数与依赖注入所需成员；保留反射 JSON 默认行为。
- 精简配置关闭调试器支持，保留异常堆栈与文件日志。没有删除安装、账号、资源或启动功能，没有改动 Java 下载和安全校验。
- JSON 反射仍会产生 IL2026 警告，第三方设计器 / 日志库也有裁剪警告；本次没有全局压制警告。显式保留模型是当前方案，不代表已经完成 Native AOT 或彻底消除反射。
- 标准界面测试为 28 项交互 + 4 项空游戏库 + 4 项旧配色迁移。又把最终链接产物的 `MCLauncher.Desktop` 及三个业务 DLL 放入隔离测试目录复测，36 项界面检查和 661 项核心检查通过。该测试保留测试工具所需的完整 Avalonia / .NET 运行环境，不能单独证明裁剪框架的所有分支均兼容。
- 对最终 EXE 另做原生窗口检查：独立目录只有一个 EXE，`DOTNET_ROOT` 指向不存在位置，首次解包、再次启动和空游戏库均出现可响应窗口；宿主确认自包含，绘图库来自本身的包缓存。真实游戏启动和干净虚拟机验收仍待完成。

裁剪依据：[.NET 官方裁剪选项](https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/trimming-options)，[Avalonia 编译绑定](https://docs.avaloniaui.net/docs/data-binding/compiled-bindings)。

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
