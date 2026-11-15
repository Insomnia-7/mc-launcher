# M0：方隅-联机 Minecraft 启动器工程审计报告

> 对应规范：`UI.md` 第 18 节「工程审计：先检查，不要直接修改」与第 32 节「Phase 1：工程审计」。
> 审计基准仓库：`C:\Users\yuanshenplayer\Desktop\gitclone\mc-launcher\mc-launcher`（分支 `UI-design`）。

---

## 1. 十八项强制检查点逐项核查结果

### 1.1 Solution 文件
仓库内并存**两个解决方案文件**，实施时必须严格区分目标工程：
1. **`MinecraftLauncher.Desktop.slnx`（本次 UI 重构唯一主目标）**：跨平台 Avalonia 桌面端解决方案，包含 `Launcher.Domain`、`Launcher.Application`、`Launcher.Infrastructure`、`Launcher.Desktop` 四个源码工程，以及 `Architecture.Check`、`Core.Checks`、`Desktop.Checks` 三个验证工程。
2. **`MinecraftLauncher.sln`（旧版保留，非本次重构目标，但共享部分文件）**：传统 Windows WPF 解决方案，包含 `Launcher.App` 与 `App.Checks`。

### 1.2 所有项目文件与分层职责
| 项目路径 | 类型 | 职责说明 |
|---|---|---|
| `src/Launcher.Domain/Launcher.Domain.csproj` | Class Library | 纯领域模型（版本解析、加载器、启动参数、资源分类、Java 模型），零第三方包依赖 |
| `src/Launcher.Application/Launcher.Application.csproj` | Class Library | 应用编排层（安装、修复、启动、整合包、账号、衣柜、持久任务、联机接口、外观/网络配置读写） |
| `src/Launcher.Infrastructure/Launcher.Infrastructure.csproj` | Class Library | 基础设施实现（HTTP 下载器、Mojang/Modrinth/CurseForge 客户端、微软鉴权、DPAPI、EasyTier 房间进程、LAN 发现） |
| `src/Launcher.Desktop/Launcher.Desktop.csproj` | `WinExe` (Avalonia) | **本次 UI 改造承载层**，程序集名 `MCLauncher.Desktop`，根命名空间 `Launcher.Desktop`，当前版本 `1.0.0-preview.7` |
| `src/Launcher.App/Launcher.App.csproj` | `WinExe` (WPF) | 旧版 WPF 客户端（`0.1.2`）；注意 `Launcher.Desktop.csproj` 通过 `<Compile Include="../Launcher.App/ViewModels/*.cs" ... Link=... />` 链接复用了其中的共享 ViewModel |
| `tests/Architecture.Check/Architecture.Check.csproj` | Console Check | ADR-001 架构护栏：强制检查 `Domain`/`Application` 无 Windows/WPF 引用、无 P/Invoke、`TargetFramework` 必须为纯 `net10.0` |
| `tests/Core.Checks/Core.Checks.csproj` | Console Check | 730+ 项核心业务与配置序列化检查（包含 `AppearanceChecks.cs`、`MultiplayerChecks.cs`、`NetworkSettingsChecks.cs`） |
| `tests/Desktop.Checks/Desktop.Checks.csproj` | Headless UI Check | 基于 `Avalonia.Headless` + Skia 离屏渲染的 180+ 项真实控件交互、绑定零错误与截图回归检查 |

### 1.3 TargetFramework 与全局编译选项
- `Directory.Build.props`：`LangVersion=latest`、`Nullable=enable`、`ImplicitUsings=enable`、`SatelliteResourceLanguages=zh-Hans;en`。
- `Launcher.Domain` / `Launcher.Application` / `Launcher.Infrastructure` / `Launcher.Desktop`：**全部为 `net10.0`**。
- `Launcher.Desktop.csproj` 额外开启了 **`<AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>`** 与 `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`——因此所有新增 AXAML 模板与页面**必须显式声明正确的 `x:DataType`**，否则编译直接报错。

### 1.4 Avalonia 实际版本
- 实际安装版本：**`12.1.4`**（完全符合 `UI.md §19` 的 `Avalonia 12.1.x` 要求，**无需升级 `.NET` 或 `Avalonia` 包版本**）。

### 1.5 NuGet 包和版本清单（`Launcher.Desktop` 与 `Launcher.Infrastructure`）
| 包名称 | 版本 | 所在项目 | 用途 |
|---|---|---|---|
| `Avalonia` | `12.1.4` | `Launcher.Desktop` | 核心 UI 框架、动画、合成层、样式系统 |
| `Avalonia.Desktop` | `12.1.4` | `Launcher.Desktop` | Windows / Linux / macOS 原生窗口宿主 |
| `Avalonia.Themes.Fluent` | `12.1.4` | `Launcher.Desktop` | Fluent v2 基础控件模板底座 |
| `Avalonia.Headless` | `12.1.4` | `Desktop.Checks` | 无头 UI 自动化交互与渲染检查 |
| `CommunityToolkit.Mvvm` | `8.4.0` | `Launcher.Desktop` / `Launcher.App` | Source Generator MVVM（`ObservableObject`, `[ObservableProperty]`, `[RelayCommand]`） |
| `Microsoft.Extensions.DependencyInjection` | `9.0.0` | `Launcher.Desktop` | DI 容器组装（`LauncherServices.cs`） |
| `Microsoft.Extensions.Logging` | `9.0.0` | `Launcher.Desktop` | 日志抽象接口 |
| `Serilog.Extensions.Logging` / `Serilog.Sinks.File` | `9.0.0` / `6.0.0` | `Launcher.Desktop` | 滚动文件日志（保留 7 天） |
| `System.Security.Cryptography.ProtectedData` | `10.0.13` | `Launcher.Infrastructure` | Windows DPAPI 账号令牌加密存储 |

### 1.6 当前 MVVM 框架与共享边界
- 使用 `CommunityToolkit.Mvvm 8.4.0` 分部类（`partial class MainViewModel : ObservableObject`）。
- **关键架构细节**：`MainViewModel` 横跨两个目录：
  - 共享核心逻辑位于 `src/Launcher.App/ViewModels/`（`MainViewModel.cs`、`MainViewModel.Accounts.cs`、`MainViewModel.Network.cs`、`MainViewModel.Setup.cs`、`DownloadViewModel*.cs`），被 WPF 与 Avalonia 同时编译。
  - Avalonia 专属逻辑位于 `src/Launcher.Desktop/ViewModels/`（`MainViewModel.Desktop.cs`、`MainViewModel.Workspace.cs`、`MainViewModel.Appearance.cs`、`MainViewModel.Multiplayer.cs`、`MainViewModel.Notices.cs` 等）。
  - **结论**：本次 UI 重构新增的视图状态属性（如 `IsHomePage`、`ThemeMode`、Toast 集合、首页快捷命令包装）应全部放入 `src/Launcher.Desktop/ViewModels/` 下的分部类中，避免破坏 `Launcher.App`（WPF）构建。

### 1.7 MainWindow 现状
- 文件：`src/Launcher.Desktop/MainWindow.axaml` 与 `MainWindow.axaml.cs`。
- 窗口尺寸：默认 `1180 × 800`，最小 `760 × 620`。
- Windows 无边框实现：已使用 Avalonia 12 原生 `ExtendClientAreaToDecorationsHint = true`、`ExtendClientAreaTitleBarHeightHint = 40`、`WindowDecorations = WindowDecorations.BorderOnly`，外层 `Grid#Shell` 绑定 `Margin="{Binding $parent[Window].OffScreenMargin}"`，标题栏使用 `WindowDecorationProperties.ElementRole="TitleBar"` 及 `MinimizeButton` / `MaximizeButton` / `CloseButton`。
- 差距：
  1. 标题栏目前缺少**当前页面面包屑/辅助标题**与**主题快速切换按钮**（`UI.md §4.2`）。
  2. 未配置 Windows 11 `TransparencyLevelHint`（Mica / AcrylicBlur）及非透明系统回退（`UI.md §28`）。
  3. 基准尺寸建议调整为更贴合 `UI.md §4.1` 的桌面比例（如默认 `1280×820` 或 `1440×900`，兼容 `1366×768`，最小尺寸验证 `960×640`）。

### 1.8 当前首页（`LibraryView.axaml`）
- 当前没有独立的 `HomeView`，启动后默认展示 `LibraryView.axaml`（「游戏库」）：
  - 顶部为 `28px`「游戏库」大标题 +「添加游戏」主按钮（占约 `78px` 高度）。
  - 下方为 `MinHeight="216"` 的大号「继续游玩」单实例卡片，再下方为搜索栏、分组下拉、卡片/列表切换和 `246px` 宽的大封面网格。
- 差距（对比 `UI.md §5, §7–§11, §25` 与确认参考图）：
  1. 缺少紧凑型 **MC 联机体验顶部横幅**（固定文案：`专注于更好的 MC 联机体验`，高度约 `80–110px`，且需较第二版参考图上移 `24–40px`）。
  2. 缺少首页首屏**双栏四象限高效工作台结构**（左上「快速启动 Minecraft」实例下拉+主启动键、右上「联机与网络状态」、左下紧凑型「游戏实例管理」列表、右下「快捷功能」矩阵）。
  3. 现有 `LibraryView` 的完整搜索/分组/收藏/卡片视图与 `GameDetailView` 详情页功能非常完善，应完整保留作为侧栏「游戏实例」页，而将首屏重构为高信息密度的 `HomeView`（「主页」）。

### 1.9 当前侧边栏
- 位于 `MainWindow.axaml` 的 `Border#SidebarPanel`，由 `MainWindow.UpdateSidebar` 管理四态（`Expanded = 236`、`Compact = 72`、`Minimal = 0`、`Overlay = 236`）。
- 差距（对比 `UI.md §6, §23`）：
  1. 展开宽度当前为 **`236px`** 且带外边距 `Margin="24,12,0,12"` 独立圆角浮动卡；规范要求展开宽度 **`240px`**、收起宽度 **`72px`**，与标题栏及内容区形成柔和的 Linear × Arc 侧栏分层。
  2. 当前收起/展开时文字在 `t=0` 直接 `IsVisible = False` 瞬时消失（见 `MainWindow.axaml:50-63` 注释），缺少 `UI.md §15, §23, §35` 明确要求的**文字淡入淡出 + 轻微水平位移 + 展开先扩宽再显字 / 收起先隐字再缩宽**动画。
  3. 已有单一 `Border#NavIndicator` 平滑移动逻辑（`MainWindow.axaml.cs:238-300`），可直接复用并优化时长至 `160–220ms`（如 `190ms`）。

### 1.10 页面导航实现
- `MainViewModel.Workspace.cs` 通过计算属性 `ShellPage` 返回当前页面标识（`"library" | "detail" | "discover" | "multiplayer" | "experience" | "settings"`）。
- `MainWindow.UpdatePage()` 缓存各页面 `UserControl` 实例（`_library`, `_discover`, `_experience`, `_preferences`, `_detail`, `_multiplayer`），切换时不销毁重建实例、不重复初始化业务数据（符合 `UI.md §24`）。
- 差距：`MainWindow.axaml:179` 当前仅用单层 `<ContentControl x:Name="PageHost" />` 瞬时替换 `Content`，**没有任何切页过渡动画**，需升级为双层容器 `AnimatedContentHost` 实现旧页 `Opacity 1→0, Y 0→-4px` 与新页 `Opacity 0→1, Y 8px→0`（`180–240ms`）。

### 1.11 主题管理方式
- 核心文件：`Launcher.Application/Settings/AppearanceSettings.cs`、`Launcher.Desktop/Appearance/DesktopThemes.cs`、`AppearanceManager.cs`、`MainViewModel.Appearance.cs`。
- **重大现状偏差**：
  1. `DesktopThemes.cs` 第 11–13 行当前写死仅提供 `glacier`（黑白灰阶日间白）**单一主题**，`MainViewModel.Desktop.cs:9` 写死 `public bool IsLightTheme => true;`，且启动时会将用户历史保存的暗色主题强制改写为 `glacier`！
  2. `AppearanceSettings.Validate()` 第 60 行校验 `ThemeId` 必须存在于 `ThemeCatalog.BuiltIns` 中，否则抛 `ArgumentException("未知主题。")`。
  3. 缺少 `ThemeMode { System, Light, Dark }` 枚举与系统深浅色自动跟随监听。

### 1.12 动画实现
- 核心文件：`src/Launcher.Desktop/Motion.cs`、`Controls/RevealPanel.cs`、`Controls/Ripple.cs`、`Controls/SelectionHighlight.cs`。
- 已有优势：
  - `CubicBezierEasing`、`EntranceEasing(0,0,0,1)`、`ExitEasing(1,0,1,1)`、`MotionPreferences`（支持 `smooth / system / off`，Win32 `SystemParametersInfo(SPI_GETCLIENTAREAANIMATION)` 检测）以及整窗 `Window.reduced-motion` 样式清空机制均已跑通。
  - `Motion.PlayStaggerAsync` 已有错峰调度雏形。
- 差距：
  1. `MotionTiming` 时长硬编码为 `83 / 167 / 250 / 420ms`，其中首屏卡片 `420ms` 超出 `UI.md §27` 要求的 `180–260ms` 区间，需建立对齐 `UI.md §27` 的 `MotionTokens`。
  2. 弹窗（`DialogLayers.axaml`）目前仅靠 `IsVisible` 瞬时显隐，缺少遮罩淡入 + `Scale 0.97→1` + 轻微位移的进退场动画。

### 1.13 资源文件与原始品牌资产
- `src/Launcher.Desktop/Assets/Brand/`：
  - `fangyu.ico` / `fangyu.png`：**原始「草方本源」立体草方块品牌 Logo**（必须保留，严禁替换）。
  - `world-meadow-v2.png`：内置高分辨率 Minecraft 草地溪流立体像素插画（可直接用于首页「专注于更好的 MC 联机体验」横幅右侧视觉层，零联网开销）。
- `src/Launcher.Desktop/Assets/Loaders/`：`vanilla.png`、`fabric.png`、`forge.png`、`forge-dark.png`、`neoforge.png`、`quilt.svg`。
- `src/Launcher.Desktop/Controls/IconCatalog.cs`：内置 32 个线性矢量图标（`FollowForeground = true`）与 18 个彩色方块图标，走 Skia 矢量缓存渲染，无需引入额外图标库。

### 1.14 游戏实例管理
- 业务模型：`InstanceCard`（含 `InstanceId`, `VersionId`, `DisplayName`, `BaseVersion`, `LoaderName`, `IsModpack`, `IsLoader`, `Subtitle`）与桌面包装类 `LibraryEntry`（含自定义显示名、分组、收藏、专属封面、上次游玩时间、运行中状态）。
- 已有命令：`OpenGameCommand`（进入实例详情）、`OpenGameDirectoryCommand`（打开游戏目录）、`OpenGameResourceFolderCommand("mods"|"resourcepacks"|"shaderpacks")`、`ExportInstanceCommand`、`OpenCloneEditorCommand`、`FavoriteGameCommand`、`DeleteInstanceCommand`。

### 1.15 Minecraft 启动逻辑
- 位于 `src/Launcher.App/ViewModels/MainViewModel.cs` 的 `LaunchCommand`（`LaunchAsync`）：负责版本解析、Java 自动查找/补齐、账号/离线身份校验、参数拼装、进程守护与 `_running` 运行态追踪，以及 `CancelCommand` 取消准备。
- **审计结论**：启动链路成熟稳定，首页「快速启动」与实例行「一键启动」必须 100% 复用 `SelectedInstance` + `LaunchCommand` + `CancelCommand`，**严禁重写任何启动业务代码**。

### 1.16 联机、网络和代理相关服务（真实能力核查）
- **联机大厅服务**（`MultiplayerViewModel.cs` + `EasyTierRoomNetwork.cs` + `LanWorldDiscovery.cs` + `GameServerStatusClient.cs`）：
  - 提供 `EngineInstalled`（EasyTier `2.6.4` 组件是否就绪）、`EngineState`、`HasSession`（是否在房间中）、`IsHost`、`SessionTitle`、`SessionState`、`SessionAddress`、`Worlds`（局域网扫描列表）。
  - `GameServerStatusClient.QueryAsync` 返回 `GameServerStatus`（含真实测量延迟 `ElapsedMilliseconds`、在线人数 `Online/Maximum`、服务端版本 `Version`）。
- **网络与代理服务**（`MainViewModel.Network.cs` + `ProxySettings.cs` + `DownloadSourceProvider.cs`）：
  - 提供 `ActiveNetworkText`（当前生效的系统代理 / 直连 / 自定义代理描述）、`ProxyModes`、以及下载源切换（官方源 / BMCLAPI）。
- **重要缺失项声明（防伪造红线）**：
  - 现有工程**没有实现 STUN / NAT 类型探测服务**（不存在 `开放 / 受限 / 对称 NAT` 数据源），且未建房/未测速时没有常驻后台 Ping 轮询（避免无意义发包）。
  - 遵循 `UI.md §10 & §25.4`：「如果当前工程没有某项能力，不得为了视觉效果伪造真实在线状态、延迟或 NAT 数据」。首页「联机与网络状态」卡片应如实展示：
    1. **联机服务**：组件未装显示 `待安装组件`（Muted/Warning），已就绪空闲显示 `组件就绪`（Info/Success），房间运行中显示 `房间在线 · 房主/成员`（Success）。
    2. **会话延迟 / 连接检测**：房间活跃或最近测速后显示实测 `XX ms`（如 `28 ms`）；空闲未测速时如实显示 `未检测（进入联机测速）`，绝不硬编码假毫秒数。
    3. **网络与下载源**：如实显示 `ActiveNetworkText`（如 `系统代理 / 直连`）与当前下载源（`官方 / BMCLAPI`），替代工程中不存在的 NAT 字段。

### 1.17 配置保存与读取
- 外观配置：`AppPaths.DataDirectory/appearance.json`（`AppearanceStore.Read/Write`，采用 `.tmp` 临时文件 + `File.Move(..., overwrite: true)` 原子写盘）。
- 游戏库展示偏好：`AppPaths.DataDirectory/library/<RootHash>.json`（按 `GameRoot` 哈希隔离存储名称、分组、收藏、封面与列表模式）。
- 代理与下载源：`AppPaths.DataDirectory/settings.json`（`ProxySettingsStore`、`DownloadSourceStore`）。

### 1.18 当前日志和异常处理方式
- 使用 `Serilog` 输出至 `AppPaths.LogsDirectory/desktop-YYYYMMDD.log`（保留 7 天），`AppDomain.CurrentDomain.UnhandledException` 捕获致命异常。
- 业务异常通过 `ErrorText.Describe(ex)` 与 `NetworkErrors.Classify(ex)` 转为友好中文提示，并通过 `MainViewModel.ShowNotice(...)` 或视图内 `Hint` 属性反馈，不弹原生阻塞式 MessageBox。

---

## 2. 组件复用、重构与新增评估汇总

### 2.1 可直接原样复用的模块（禁止破坏）
- `Launcher.Domain`、`Launcher.Application`（除 `AppearanceSettings` 扩展 `ThemeMode` 字段外）、`Launcher.Infrastructure` 全部业务服务与持久化逻辑。
- `LauncherServices.cs` DI 注册图、`App.axaml.cs` 单实例互斥锁与首帧后后台初始化（`StartBackgroundWork`）。
- 品牌与加载器资产：`Assets/Brand/fangyu.png`、`fangyu.ico`、`world-meadow-v2.png`、`Assets/Loaders/*`、`Controls/IconCatalog.cs`、`UiIcon`、`InstanceTypeIcon`、`PlayerGlPreview`（3D OpenGL 皮肤预览）。
- 子页面业务视图：`DiscoverView`、`MultiplayerView`、`ExperienceView`（角色衣柜 + 下载任务）、`GameDetailView`（实例详情 6 个子页签）。

### 2.2 需要重构的模块
1. `Appearance/DesktopThemes.cs` & `AppearanceManager.cs` & `MainViewModel.Appearance.cs`：从“仅日间白灰阶”重构为 Linear × Arc 深浅双色板 + `ThemeMode (System/Light/Dark)` 动态切换。
2. `Styles/Controls.axaml`：接入 `UI.md §12–§14` 语义化颜色 Key、4px 间距与圆角分级、8 态按钮样式、卡片微边框样式、450ms Tooltip 样式。
3. `MainWindow.axaml` & `MainWindow.axaml.cs`：升级 38px 轻量标题栏（含当前页名与主题快切）、Mica 透明回退、`240px ↔ 72px` 双态侧边栏（含文字分步淡入淡出位移）、挂载 `AnimatedContentHost` 与右下角 `ToastHost`。
4. `Motion.cs`：校准为 `UI.md §27` 的 `MotionTokens`，补充可取消的弹窗缩放过渡与页面切换动画辅助方法。

### 2.3 需要新增的组件
1. `Views/HomeView.axaml` (+ `.cs`) 与 `ViewModels/MainViewModel.Home.cs`：实现顶部横幅上移后的全新首页布局（与完整 `LibraryView` 解耦，「主页」聚焦快速启动与高频概览，「游戏实例」保留全量库管理）。
2. `Controls/AnimatedContentHost.cs`：统一管理旧页退场（`Opacity 1→0, Y 0→-4px`）与新页进场（`Opacity 0→1, Y 8px→0`）及快速连点中断恢复。
3. `Controls/ToastHost.axaml` (+ `ViewModels/ToastManager.cs`)：右下角多条队列式四态通知（`Success / Error / Warning / Info`），替代原先单条挤压布局的底部横条（同时兼容旧 `ShowNotice` 调用自动转发至 Toast）。
4. `Views/UiShowcaseView.axaml` (+ `.cs`)：仅在开发环境变量 `FANGYU_DEV_SHOWCASE=1` 下开启的组件与动效验收预览页。

---

## 3. Avalonia 12.1.4 兼容性与工程护栏注意事项

1. **编译期绑定强制要求（`AvaloniaUseCompiledBindingsByDefault=true`）**：
   - 每个 `UserControl` / `DataTemplate` 必须声明精确的 `x:DataType`；跨层级访问父级 `MainViewModel` 命令时沿用现有工程写法 `$parent[UserControl].((vm:MainViewModel)DataContext).XXXCommand`。
2. **ADR-001 架构红线（`tests/Architecture.Check`）**：
   - 严禁在 `Launcher.Domain` 或 `Launcher.Application` 中引入 `Avalonia.*`、`Microsoft.Win32` 或任何 `[DllImport]`。系统主题检测（`IPlatformSettings`）与窗口材质控制必须严格留在 `Launcher.Desktop` 层。
3. **现有 `Desktop.Checks` 回归兼容点**：
   - `Desktop.Checks/Program.cs:107` 的 `--legacy-theme` 检查断言了旧单主题逻辑（`model.ThemeChoices.Count == 1`），`WorkspaceUiChecks.cs:17` 检查了主导航按钮名（`LibraryNav`, `DiscoverNav`, `WardrobeEntry`, `TaskCenterEntry`, `SettingsEntry`）。在 M2 / M4 / M9 演进时，保留既有控件 `x:Name` 以最大化向后兼容，并在 M9 同步更新主题数量与新增 `HomeNav` 的断言检查。
4. **Mica 材质与自定义壁纸互斥处理**：
   - 当用户未设置自定义壁纸且运行在 Windows 11 时，启用 `WindowTransparencyLevel.Mica` 配合高不透明度底色（深色 `#E6101114` / 浅色 `#EBF7F8FA`）；当用户导入了本地壁纸（`WallpaperImage != null`）或运行在 Win10 / Linux / Headless 环境时，自动回退为实色 `BgBrush` 衬底，确保文字对比度始终 ≥ 4.5:1。
