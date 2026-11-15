# 方隅-联机 Minecraft 启动器 UI 重构分阶段实施计划（M0 → M9）

> 基准规范：`UI-design/UI.md`（V1.1）与确认参考图 `UI-design/方隅 Minecraft 启动器 UI 设计方案.png`
> 目标工程：`mc-launcher/src/Launcher.Desktop`（C# / .NET 10 / Avalonia 12.1.4 / CommunityToolkit.Mvvm 8.4.0）
> 实施原则：**先审计后动工、先资源后结构、先容器后页面、每阶段可独立编译与回归验证、零破坏既有 Minecraft 启动与管理业务**。

---

## 全局路线图总览

```text
M0 工程基线审计（只读冻结）
 └─► M1 设计系统与双主题 Token（颜色 / 字体 / 4px间距 / 分级圆角）
      └─► M2 三态主题管理（System/Light/Dark）与 Mica 安全材质层
           ├─► M3 无边框窗口与 38px 自定义标题栏（原始 Logo / 页标题 / 主题快切）
           ├─► M4 侧边栏双态（240px ↔ 72px / 文字分步动画 / 平滑指示条 / Tooltip）
           └─► M5 统一动效系统（MotionTokens / AnimatedContentHost / 7段交错入场）
                └─► M6 首页重构（横幅上移 28-36px / 快速启动 / 真实联机状态 / 实例列表 / 快捷入口）
                     └─► M7 全局交互组件（8态按钮 / 缩放淡入弹窗 / 队列式四态 Toast）
                          └─► M8 开发模式 UI Showcase 预览页（环境门控隔离）
                               └─► M9 全量构建、Headless 自动化测试与 Windows 实机多分辨率验收
```

---

## M0：工程审计与业务契约冻结（对应 `UI.md` Phase 1 / §18）

### 1. 目标
在不修改任何一行代码的前提下，摸清 Solution 双前端结构、Avalonia 12.1.4 可用 API、MVVM 共享边界及全部真实业务数据源，输出审计报告并冻结红线。

### 2. 核心产出
- 已完成并落盘于 `./00-M0-engineering-audit.md`。

### 3. 冻结的关键工程约束
1. **只改 `Launcher.Desktop`（及 `AppearanceSettings` 必要的向后兼容枚举扩展）**，不动 `GameInstaller`、`VersionLoader`、`ModpackInstaller`、`EasyTierRoomNetwork`、`MainViewModel.LaunchAsync` 等核心业务链路。
2. **保留原始草方块品牌 Logo**（`Assets/Brand/fangyu.png` 与 `fangyu.ico`），绝不使用参考图左上角的紫色山峰占位符。
3. **如实映射联机与网络数据**：不伪造工程未实现的 STUN NAT 类型（`开放/受限`），改用工程真实的「联机引擎/房间会话状态 + 实测服务器/房间延迟 + 当前代理与下载源模式」三行指标。
4. **完整保留既有业务入口**：侧栏除参考图展示的「主页 / 游戏实例 / 联机大厅 / 发现资源 / 设置」外，完整保留现有工程已交付的「角色（3D 衣柜）」与「下载任务（断点续传任务中心）」，不删减任何已有用户能力。

---

## M1：设计系统与深浅双主题资源层（对应 `UI.md` Phase 2 / §12–§14, §20）

### 1. 目标
建立以 **Linear × Arc 为主（65%–75%）、Fluent 2 状态表达为辅** 的统一设计 Token 体系，彻底消除散落硬编码色值，确保深色与浅色模式共享同一套语义化 Resource Key。

### 2. 涉及文件
- 修改：`src/Launcher.Application/Settings/AppearanceSettings.cs`（扩展 `ThemeCatalog.ColorKeys` 与内置 `fangyu-dark` / `fangyu-light` 预设，兼容旧 ID 自动映射）
- 修改：`src/Launcher.Desktop/Appearance/DesktopThemes.cs`
- 新增：`src/Launcher.Desktop/Styles/Tokens.axaml`（字体、字号阶梯、4px 间距、圆角分级）
- 修改：`src/Launcher.Desktop/App.axaml`（更新 `FluentTheme.Palettes` 强调色与样式引入顺序）
- 参考样例：`samples/M1-DesignSystem/DesktopThemes.Sample.cs`、`samples/M1-DesignSystem/ThemeTokens.Sample.axaml`

### 3. 规范数值落地表
1. **双主题语义色值（对齐 `UI.md §12 & §13` 并兼容现有 Brush Key）**：
   | 规范 Token | 工程统一资源 Key | Dark 色值 | Light 色值 | 对比度说明 |
   |---|---|---|---|---|
   | Background Primary | `BgBrush` | `#101114` | `#F7F8FA` | 主内容底色 |
   | Background Sidebar | `SidebarBgBrush` / `PanelBrush` | `#14161B` | `#F1F2F5` | 侧边栏与标题栏底色 |
   | Surface | `CardBrush` | `#191C22` | `#FFFFFF` | 标准卡片表面 |
   | Surface Elevated | `SurfaceElevatedBrush` / `FieldBrush` | `#1F232B` | `#FAFAFC` | 浮层、下拉框、次级块 |
   | Hover | `HoverBrush` | `#262B35` | `#EBEDF2` | 悬停态背景 |
   | Pressed | `PressedBrush` | `#303642` | `#E1E4EB` | 按压态背景 |
   | Border | `LineBrush` | `#2A2E37` | `#E5E7ED` | 1px 卡片与输入框主边框 |
   | Border Subtle | `BorderSubtleBrush` | `#22262E` | `#ECEEF2` | 内部分割细线 |
   | Text Primary | `TextBrush` | `#F1F2F5` | `#1D2028` | 主文案（对比度 > 13:1） |
   | Text Secondary | `MutedBrush` | `#A0A4AF` | `#626A78` | 次要说明（对比度 > 5.3:1） |
   | Text Muted | `FaintBrush` | `#737987` | `#89909D` | 辅助标签/时间戳 |
   | Accent Primary | `AccentBrush` / `AccentFillBrush` | `#8B7CFF` | `#6E60DB` | 品牌蓝紫强调（注意深色按钮白字对比度校准，实心填充配 `#FFFFFF` 文字时使用 `#6E5EE8` 或加深底色确保 ≥ 4.5:1） |
   | Accent Hover | `AccentHoverBrush` | `#9A8DFF` | `#5F52CB` | 主按钮悬停 |
   | Accent Pressed | `AccentPressedBrush` | `#7466E5` | `#5044B9` | 主按钮按压 |
   | Accent Subtle | `AccentSubtleBrush` / `NavSelectedBrush` | `#292440` | `#EEEBFF` | 导航选中胶囊底色、微标签底色 |
   | Success / Warning / Error | `SuccessBrush` / `WarnBrush` / `DangerBrush` | `#4FBF83` / `#E6B65C` / `#E06C75` | `#269B65` / `#B98225` / `#D34F5D` | 仅用于真实状态表达，严禁拿绿色做常规装饰 |
2. **排版、间距与圆角分级（`UI.md §14`）**：
   - 字体栈：`Segoe UI Variable, Microsoft YaHei UI, PingFang SC, Noto Sans CJK SC, Segoe UI, sans-serif`（兼顾 Windows 11 西文数字精致感与中文清晰度）。
   - 字号阶梯：页面标题 `26px`、区域标题 `18px`、卡片标题 `15px`、正文 `14px`、辅助文字 `12px`、状态胶囊标签 `11px`。
   - 间距（严格 4px 倍数）：`4 / 8 / 12 / 16 / 20 / 24 / 32 / 40`。
   - 圆角分级（禁止所有控件同圆角）：小型标签/复选框 `6px`、按钮/输入框/导航项 `8px`、业务卡片/横幅 `12px`、模态弹窗 `16px`。

### 4. 验收标准
- 编译零警告，全工程 `Styles/Controls.axaml` 与新增视图零裸写 `#RRGGBB` 业务色值。

---

## M2：三态主题管理（Dark / Light / System）与 Mica 材质层（对应 `UI.md` Phase 8 / §21, §28）

### 1. 目标
实现 `ThemeMode { System, Light, Dark }` 运行时无闪烁动态切换、系统深浅色实时跟随、`appearance.json` 持久化恢复，以及 Windows 11 适量 Mica / Acrylic 材质与 Win10 实色降级回退。

### 2. 涉及文件
- 修改：`src/Launcher.Application/Settings/AppearanceSettings.cs`（新增 `string ThemeMode = "dark"` 属性并校验 `"system" | "light" | "dark"`，保留旧字段默认值以 100% 兼容旧 `appearance.json` 反序列化）
- 修改：`src/Launcher.Desktop/Appearance/AppearanceManager.cs`
- 修改：`src/Launcher.Desktop/ViewModels/MainViewModel.Appearance.cs`、`MainViewModel.Desktop.cs`
- 修改：`src/Launcher.Desktop/Views/PreferencesView.axaml`（外观页提供「跟随系统 / 深色模式 / 浅色模式」三态分段切换器）
- 参考样例：`samples/M2-ThemeAndMica/ThemeModeAndAppearanceManager.Sample.cs`

### 3. 核心实现要点
1. **运行时无损切换（不重建窗口）**：
   - 沿用并升级 `AppearanceManager` 中对 `Application.Current.Resources` 内共享 `SolidColorBrush` 实例的原地 `Color` 更新与 `ColorTransition`（`200ms Linear`），同步切换 `app.RequestedThemeVariant`（`ThemeVariant.Dark` / `ThemeVariant.Light`），所有已挂载页面、输入框内容、滚动位置与任务状态 100% 保留。
2. **跟随系统主题（Avalonia 12 标准 API）**：
   - 通过 `Application.Current?.PlatformSettings` 读取 `GetColorValues().ThemeVariant`（`PlatformThemeVariant.Dark` vs `Light`），并订阅 `PlatformSettings.ColorValuesChanged` 事件；当 `CurrentThemeMode == ThemeMode.System` 时自动响应系统日夜切换，且在 `App.Exit` 时显式退订事件（遵循 `UI.md §31` 严禁事件泄漏）。
3. **Windows Mica / Acrylic 克制材质与回退（`UI.md §3.3 & §28`）**：
   - 在 `MainWindow`（仅 Windows 且用户未开启自定义图片壁纸时）设置：
     `TransparencyLevelHint = [WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.None]`。
   - 监听 `ActualTransparencyLevelChanged`：当系统实际生效 `Mica` / `AcrylicBlur` 时，仅让**标题栏与侧边栏外层容器**使用高不透明度半透明底色（深色 `#DE14161B`、浅色 `#E6F1F2F5`），而主内容区的业务卡片（`CardBrush`）保持实色或 `96%+` 不透明度，确保文字对比度不受桌面花哨壁纸干扰；在 Win10 或不支持透明材质的环境自动回退为 `100%` 不透明实色 `#101114` / `#F7F8FA`。

### 4. 验收标准
- 在任意子页面连续点击切换 `Dark → Light → System`，不重建 `MainWindow`、不丢失当前选中实例或搜索词，重启后精确恢复上次所选 `ThemeMode`。

---

## M3：轻量无边框窗口与自定义标题栏（对应 `UI.md` Phase 3 / §4.2, §22）

### 1. 目标
打造 `38px` 高的轻量一体化 Windows 无边框窗口顶栏，融合左侧原始品牌标识、当前页面名称/辅助上下文、右侧主题快速切换入口与标准三颗窗口控制按钮，完整保留原生拖动、双击最大化、边缘缩放与 Windows 11 Snap Layouts。

### 2. 涉及文件
- 新增（或拆分自 `MainWindow`）：`src/Launcher.Desktop/Views/Shell/CustomTitleBar.axaml` (+ `.cs`)
- 修改：`src/Launcher.Desktop/MainWindow.axaml`、`MainWindow.axaml.cs`
- 参考样例：`samples/M3-WindowAndTitleBar/CustomTitleBar.Sample.axaml`

### 3. 核心实现要点
1. **Avalonia 12.1.4 原生窗口装饰契约**：
   - 保持经过 `Desktop.Checks` 实测验证的 `ExtendClientAreaToDecorationsHint = true`、`ExtendClientAreaTitleBarHeightHint = 38`、`WindowDecorations = WindowDecorations.BorderOnly`。
   - 根容器绑定 `Margin="{Binding $parent[Window].OffScreenMargin}"`，彻底消除最大化时内容超出屏幕边缘 7–8px 的经典无边框 Bug。
   - 空白拖拽区声明 `WindowDecorationProperties.ElementRole="TitleBar"`（原生支持拖动与双击最大化/还原）；右侧操作区声明 `ElementRole="User"`，三颗系统键分别声明 `MinimizeButton`、`MaximizeButton`（原生支持 Win11 悬停分屏浮层）、`CloseButton`。
2. **顶栏信息组织（轻量不臃肿）**：
   - 左侧：侧栏收起或极窄态时显示 `fangyu.png`（20×20）+ `方隅`，紧随 `1px` 分割线与当前页面标题（如 `主页` / `游戏实例` / `联机大厅`，字号 `13px`，直接承接 `UI.md §5.2` 顶栏辅助信息，从而省去首页内容区上方冗余的大标题留白）。
   - 右侧：**主题快切按钮**（点击在 `Dark ↔ Light ↔ System` 间循环或弹出轻量 Flyout，带当前模式图标与 Tooltip）+ 任务后台运行微指示器（如有下载任务进行中）+ 最小化 / 最大化（还原） / 关闭按钮（`42 × 32px`，关闭键悬停 `#E06C75` / `#C42B1C` 红底白字）。
   - 窗口非最大化时，外层叠加 `1px` `IsHitTestVisible="False"` 的内描边 `BorderSubtleBrush`，解决深色窗口在深色壁纸上边界模糊的问题。

### 4. 验收标准
- 标题栏按钮点击绝不误触发窗口拖动；双击标题栏空白处平滑最大化/还原；100% / 125% / 150% / 200% DPI 缩放下无裁切、无最大化溢边。

---

## M4：侧边栏双态（240px ↔ 72px）与连续滑动指示条（对应 `UI.md` Phase 4 / §6, §23）

### 1. 目标
严格实现 `Expanded = 240px` 与 `Collapsed = 72px` 双态侧边栏（极窄窗口保留 Overlay 兜底），补齐文字淡入淡出与微位移时序动画、单一连续平滑移动选中指示条、收起态居中图标与 Tooltip，且全程保留原始品牌图标。

### 2. 涉及文件
- 修改：`src/Launcher.Desktop/ViewModels/MainViewModel.Appearance.cs`（统一暴露 `IsSidebarExpanded` 与现有 `IsSidebarCollapsed` 双向联动，`SidebarWidth => IsSidebarExpanded ? 240 : 72`）
- 修改：`src/Launcher.Desktop/MainWindow.axaml`、`MainWindow.axaml.cs`
- 参考样例：`samples/M4-Sidebar/Sidebar240To72.Sample.axaml`、`samples/M4-Sidebar/SidebarController.Sample.cs`

### 3. 核心实现要点
1. **导航项映射（遵循现有工程真实模块，不丢功能）**：
   - `HomeNav`：**主页**（新增首页概览与快速启动）
   - `LibraryNav`：**游戏实例**（现有完整游戏库与实例详情工作区）
   - `MultiplayerNav`：**联机大厅**（现有局域网发现 + EasyTier 房间联机）
   - `DiscoverNav`：**下载中心**（现有 Modrinth / CurseForge 模组、整合包、资源包、光影、原版与加载器安装）
   - `WardrobeEntry`：**角色衣柜**（现有正版/离线皮肤与 3D 披风预览）
   - `TaskCenterEntry`：**任务中心**（现有持久化下载/导入任务队列，带角标计数）
   - `SettingsEntry`：**设置**（含外观、网络与连接、存储管理、更新、设置同步；同时在首页「联机与网络状态」卡片提供直达「设置 → 网络」的快捷跳转）
   - 底部固定区：启动器新版本提示条（有更新时显示）+ 当前游戏账号头像与身份切换入口（收起至 `72px` 时仅保留居中 `32×32` 玩家头像按钮与 Tooltip）。
2. **展开 / 收起分步时序动画（总时长 `250ms`，位于 `220–280ms` 规范区间）**：
   - **收起（240px → 72px）**：
     - `t = 0ms`：导航文字与品牌副标题启动 `120ms` 淡出（`Opacity 1 → 0`）+ 水平微移（`TranslateX 0 → -6px`），同时侧边栏容器宽度开始 `250ms EntranceEasing` 从 `240` 过渡至 `72`。
     - `t = 120ms`：文字透明后切换紧凑居中内边距，图标平滑落位至 `72px` 水平正中，绝不发生文字硬裁切或图标跳闪。
   - **展开（72px → 240px）**：
     - `t = 0ms`：先恢复展开态内边距并启动侧边栏宽度 `72 → 240`（`250ms`），文字初始保持 `Opacity = 0, TranslateX = -6px`。
     - `t = 80ms`：宽度已拉开足够空间后，文字执行 `140ms` 淡入（`Opacity 0 → 1`）+ 位移归零（`TranslateX -6px → 0`）。
   - 使用单实例 `CancellationTokenSource` 管理：用户狂点折叠按钮时立即取消上一段补间并从当前中间值无缝反向过渡。
3. **单一连续选中提示条（`NavIndicator`）**：
   - 整个导航列表共享唯一一个 `3px × 20px` 圆角竖条（`AccentBrush`）与选中项 `AccentSubtleBrush` 背景胶囊过渡。
   - 切换导航时通过 `TranslateTransform.Y` 在 `190ms`（`160–220ms`）内按 `EntranceEasing` 平滑滑动至目标项垂直中心，不闪烁、不跳回顶部。
4. **收起态 Tooltip**：
   - 收起态（`!IsSidebarExpanded`）启用 `ToolTip.ShowDelay="450"`，悬停展示中文模块名；展开态自动抑制重复导航文字 Tooltip 以保持克制。

---

## M5：统一动效 Token 与页面切换 / 交错入场容器（对应 `UI.md` Phase 5 / §15, §24, §27, §30）

### 1. 目标
建立单一事实来源的 `MotionTokens`，实现不闪白、可中断的页面切换容器 `AnimatedContentHost` 与首屏 7 段内容交错入场机制，并完整支持「减少动画」可访问性降级。

### 2. 涉及文件
- 修改：`src/Launcher.Desktop/Motion.cs`（升级并对齐 `UI.md §27` 参数表）
- 新增：`src/Launcher.Desktop/Controls/AnimatedContentHost.cs`
- 修改：`src/Launcher.Desktop/MainWindow.axaml`（以 `AnimatedContentHost` 替换裸 `ContentControl#PageHost`）
- 参考样例：`samples/M5-NavigationAndMotion/MotionTokensAndPageHost.Sample.cs`

### 3. 核心实现要点
1. **`MotionTokens` 参数集中定义（严格落入 `UI.md §27` 区间，禁止页面私自写毫秒数）**：
   - `Hover` = `120ms`（规范 `100–140ms`）
   - `Pressed` = `85ms`（规范 `70–100ms`）
   - `Release` = `140ms`（规范 `120–160ms`）
   - `NavIndicator` = `190ms`（规范 `160–220ms`）
   - `Sidebar` = `250ms`（规范 `220–280ms`）
   - `TextFade` = `140ms`（规范 `120–160ms`）
   - `PageSwitch` = `200ms`（规范 `180–240ms`）
   - `ContentEntrance` = `220ms`，相邻错峰 `StaggerStep = 40ms`（规范 `180–260ms`，延迟 `35–55ms`）
   - `Dialog` = `220ms`（规范 `180–260ms`）
   - `Tooltip` = `140ms`，延迟 `450ms`（规范 `120–160ms`，延迟 `400–500ms`）
2. **双层防闪白 `AnimatedContentHost`（`UI.md §15 & §24`）**：
   - 内部维护 `Background = BgBrush` 的根 `Grid` 及 `_outgoingPresenter` / `_incomingPresenter` 两层轻量 `ContentPresenter`。
   - 切换页面时：
     - 旧页面执行：`Opacity 1 → 0` + `TranslateY 0 → -4px`（`110ms ExitEasing`）；
     - 新页面叠加在上方执行：`Opacity 0 → 1` + `TranslateY 8px → 0`（`200ms EntranceEasing`）；
     - 底层宿主实色 `BgBrush` 托底，杜绝任何黑屏、白屏或桌面透底闪烁。
   - **快速连续切换防护**：每次切页递增 `_switchVersion` 并 `Cancel()` 上一次 `CancellationTokenSource`；在 `finally` 中强制将旧层清空（`Content = null; IsVisible = false`）、将当前层归位为 `Opacity = 1, TranslateY = 0`，无论每秒连点多少次导航都不会叠页或卡死。
   - **视图缓存不重刷**：`MainWindow` 继续持有各页面 `UserControl` 单例引用，切页仅切换视觉挂载，不重新拉取网络或重置用户筛选状态。
3. **内容交错入场（7 段序列）与减少动画（`UI.md §15 & §30`）**：
   - 首页首次挂载时按 `UI.md §15` 顺序触发 `Motion.PlayStaggerSequenceAsync`：
     `① 顶栏页标 → ② 首页横幅 → ③ 快速启动卡 → ④ 联机与网络状态卡 → ⑤ 游戏实例管理卡 → ⑥ 快捷功能卡 → ⑦ 底部辅助状态行`（每段 `220ms`，间隔 `40ms`，全套在 `~460ms` 内干脆收尾，且动画期间 `IsHitTestVisible="True"` **绝不阻塞用户立即点击「启动游戏」**）。
   - 开启减少动画（`!MotionPreferences.Enabled`）时：跳过所有交错延迟与 Y 轴位移，切页缩短为 `80ms` 纯透明度交叉或瞬时切换，仅保留按钮基础状态变色。

---

## M6：首页空间重算与核心业务重构（对应 `UI.md` Phase 6 / §5, §7–§11, §25）

### 1. 目标
严格对标第二版确认参考图并落实本轮最关键的空间调整：**将顶部宣传横幅整体上移约 `28–36px`，压缩横幅上方冗余留白，重新分配垂直网格预算**，让首屏完整、宽裕地展示快速启动、联机与网络状态、更多真实游戏实例行以及快捷功能入口。

### 2. 涉及文件
- 新增：`src/Launcher.Desktop/Views/HomeView.axaml`、`HomeView.axaml.cs`
- 新增：`src/Launcher.Desktop/ViewModels/MainViewModel.Home.cs`（轻量桥接属性与快捷动作命令，100% 委托现有 Service/Command）
- 修改：`src/Launcher.Desktop/ViewModels/MainViewModel.Workspace.cs`（新增 `"home"` 作为默认启动 `ShellPage`，原 `"library"` 对应侧栏「游戏实例」全量管理页）
- 修改：`src/Launcher.Desktop/ViewModels/MultiplayerViewModel.cs`（暴露只读 `LastPingMs` 实测延迟属性供首页状态卡绑定）
- 参考样例：`samples/M6-HomeLayout/HomeView.Sample.axaml`、`samples/M6-HomeLayout/MainViewModel.Home.Sample.cs`

### 3. 垂直空间重算对比（核心布局改造依据）

| 纵向区域 | 第二版参考图 / 旧版留白估算 | **M6 优化后尺寸（逻辑像素）** | 调整说明 |
|---|---|---|---|
| 窗口自定义标题栏 | `40px` | **`38px`** | 内置当前页面名「主页」，省去内容区独立 H1 行 |
| 横幅上方内容区内边距 | `36–44px`（旧 `LibraryView` 含标题高达 `90px`） | **`8px`** | **横幅整体上移 `28–36px`**，紧贴内容区顶部但与标题栏留出呼吸缝 |
| **MC 联机体验横幅** | `~116px` | **`88px`**（`MinHeight=84, MaxHeight=96`） | 保留固定文案「专注于更好的 MC 联机体验」与「更稳定 · 更便捷 · 更流畅」，右侧融合内置 `world-meadow-v2.png` 渐变叠层，不做商城大广告 |
| 模块行间距 | `20–24px` | **`14px`**（符合 4px 附近紧凑节奏或标准 `16px`） | 卡片间距统一 `16px` |
| 第一行：快速启动 (`1.65*`) + 联机状态 (`1*`) | `~168px` | **`144px`** | 左卡：实例选择下拉框 + 醒目主操作「启动游戏」按钮 + 准备/运行中进度反馈；右卡：三行真实状态指标 |
| 第二行：游戏实例管理 (`1.65*`) + 快捷功能 (`1*`) | 被上方挤压，仅显 2–3 项 | **自适应填满剩余高度 (`*`)** | 在 `1440×900` 下可用高度达 **`~550px`**（首屏展示 **8–9 个**紧凑实例行）；在 `1366×768` 下可用高度达 **`~418px`**（首屏仍可完整展示 **5–6 个**实例行 + 右侧全部 6 个快捷按钮！） |
| 底部辅助信息行 | 大块留白 | **`28px`** | 展示当前游戏根目录摘要、Java 环境状态与最近启动信息，底部无废空白 |

### 4. 四大核心业务卡片真实绑定清单
1. **快速启动 Minecraft（左列上）**：
   - 实例下拉框：`ItemsSource="{Binding LibraryEntries}"`，切换时同步写回 `SelectedInstance`；显示实例图标（封面或 `InstanceTypeIcon`）、名称、MC 版本与加载器标签。
   - 主操作按钮：直接绑定 `Command="{Binding LaunchCommand}"`；空库时切换为「新建首个实例」（`OpenSetupCommand`）；准备/启动中（`IsBusy == true`）显示内联微加载指示、阶段进度条（`ProgressPercent` + `StatusText`）与「取消」按钮（`CancelCommand`）。
2. **联机与网络状态（右列上，图标 + 颜色 + 文字三合一表达）**：
   - 行 1 **联机服务**：绑定 `HomeMultiplayerStateText` + 状态圆点/图标（房间在线 = `SuccessBrush` `#4FBF83`/`#269B65`；组件已装待命 = `AccentBrush`；组件未装 = `MutedBrush`），点击可一键跳转联机大厅。
   - 行 2 **会话延迟**：当联机房间或测速返回真实 `LastPingMs` 时显示 `优秀 (XX ms)` / `一般 (XX ms)`；未测速时如实显示 `未测速`（`MutedBrush`），绝不编造假延迟。
   - 行 3 **网络模式**：如实绑定 `ActiveNetworkText` 与当前下载源（如 `直连 · BMCLAPI` / `系统代理 · 官方源`），点击直达「设置 → 网络」。
3. **游戏实例管理（左列下）**：
   - 标题栏右侧「全部实例 (`{Instances.Count}`) ›」点击切换至侧栏「游戏实例」完整页。
   - 列表项绑定真实 `LibraryEntries`：左侧 `36×36` 实例图标、实例名、克制的 `BaseVersion` 与 `Loader` 胶囊标签；右侧 Hover/常驻显示快捷启动图标按钮 `▷`（选中该实例并调用 `LaunchCommand`）与更多菜单 `•••`（管理实例、模组管理、打开游戏目录、导出备份、创建副本、删除实例）。
   - 空库时展示清晰的空状态引导卡（「暂无游戏实例，点击新建或导入整合包」）。
4. **快捷功能（右列下，2×3 紧凑网格，全部对应现有已实现能力）**：
   - `新建实例` → `OpenSetupCommand`
   - `导入整合包` → `ImportModpackCommand`
   - `管理模组` → 打开当前选中实例详情的 `ModsDetailTab`（无实例时跳下载中心模组分类）
   - `管理资源包` → `OpenGameResourceFolderCommand("resourcepacks")`
   - `打开游戏目录` → `OpenGameDirectoryCommand`
   - `日志与诊断` → 打开当前实例 `DiagnosticsDetailTab` 或 `OpenLogsCommand`

---

## M7：全局组件系统（按钮 / 卡片 / 弹窗 / Toast / Tooltip）（对应 `UI.md` Phase 7 / §15, §26）

### 1. 目标
统一全应用按钮 8 种交互状态、模态弹窗中心缩放淡入退场动画、450ms 延迟轻位移 Tooltip，以及右下角有序排列的四态 Toast 通知队列。

### 2. 涉及文件
- 修改：`src/Launcher.Desktop/Styles/Controls.axaml`
- 修改：`src/Launcher.Desktop/Views/DialogLayers.axaml`、`DialogLayers.axaml.cs`
- 新增：`src/Launcher.Desktop/ViewModels/ToastManager.cs`、`src/Launcher.Desktop/Views/Shell/ToastHost.axaml`
- 修改：`src/Launcher.Desktop/ViewModels/MainViewModel.Notices.cs`（将原有 `ShowNotice` 无缝接入新 `ToastManager`，保持既有业务调用点零改动）
- 参考样例：`samples/M7-Components/ButtonsDialogsToast.Sample.axaml`、`samples/M7-Components/ToastService.Sample.cs`

### 3. 核心实现要点
1. **统一按钮系统（8 态完备 + 防连点）**：
   - 变体：`Button.primary`（蓝紫实心主按钮）、`Button`（1px 细边框次按钮）、`Button.ghost` / `Button.icon`（轻量图标按钮）、`Button.danger-solid`（危险确认按钮）。
   - 8 态视觉与时序：
     - `Normal` → `Hover`（`120ms` 背景/边框平滑过渡）
     - `Pressed`（`85ms` `scale(0.98)` 下压，松开 `140ms` 自然恢复）
     - `Focused`（`:focus-visible` 瞬时显示 `2px` `AccentBrush` 外焦点环）
     - `Disabled`（`Opacity 0.45`，瞬时灰化不播残影）
     - `Loading` / `Success` / `Error`：通过伪类或样式类 `.is-loading`（显示旋转 `StatusSpinner` 并拦截二次点击，`CommunityToolkit.Mvvm` `[RelayCommand]` 默认自带异步防重入 `AllowConcurrentExecutions = false`）、`.is-success`（`SuccessBrush` 勾号反馈）、`.is-error`（`DangerBrush` 警示态）。
2. **弹窗动画（`DialogLayers` 遮罩淡入 + `Scale 0.97 → 1` + `Y 6px → 0`）**：
   - 打开模态框时：半透明遮罩 `OverlayBrush` 执行 `Opacity 0 → 1`（`180ms`），弹窗主体 `Border.modal-surface`（圆角 `16px`）执行 `Opacity 0 → 1`、`Scale 0.97 → 1.0`、`TranslateY 6px → 0`（`220ms EntranceEasing`）；关闭时执行 `150ms ExitEasing` 反向退场后再置 `IsVisible = false`。
3. **右下角队列式 Toast 通知**：
   - 固定锚定在主窗口右下角（`Margin="0,0,20,20"`，`ZIndex="30"`），支持 `Success / Error / Warning / Info` 四种语义（各自匹配独立状态图标 + 语义色条 + 清晰文案，不仅靠颜色区分）。
   - 支持最多同时堆叠 4 条通知有序排列、单条手动 `×` 关闭、可点击动作按钮（如「查看任务」）以及默认 `5s` 自动淡出移除。

---

## M8：UI Showcase 开发预览页（对应 `UI.md` §29）

### 1. 目标
提供仅供开发与视觉走查使用的独立预览页，一站式检视深浅色切换、按钮 8 态、侧边栏 `240/72` 伸缩、选中提示条、Tooltip、弹窗、四态 Toast、卡片与交错入场动效。

### 2. 涉及文件
- 新增：`src/Launcher.Desktop/Views/Dev/UiShowcaseView.axaml`、`UiShowcaseView.axaml.cs`
- 修改：`src/Launcher.Desktop/MainWindow.axaml`（仅当 `Environment.GetEnvironmentVariable("FANGYU_DEV_SHOWCASE") == "1"` 时在侧栏底部显示「UI 预览 (Dev)」入口或绑定快捷键 `Ctrl+Shift+D`，**普通用户正式环境完全隐藏**）
- 参考样例：`samples/M8-ShowcaseAndChecks/UiShowcaseView.Sample.axaml`

---

## M9：构建、自动化测试与 Windows 实机验收（对应 `UI.md` Phase 9 / §31, §33, §34）

### 1. 目标
严格执行编译、架构护栏检查、核心单测、Headless UI 自动化测试与 Windows 多分辨率/多 DPI 实机验证，并如实区分「已编译通过 / 已通过自动化测试 / 已实机运行验证」。

### 2. 必跑构建与测试命令清单
在 `mc-launcher/mc-launcher` 目录下依次执行：
```powershell
# 1. 恢复依赖与双解决方案构建（确保共享 ViewModel 未破坏旧 WPF 工程）
dotnet restore MinecraftLauncher.Desktop.slnx
dotnet build MinecraftLauncher.Desktop.slnx -c Release
dotnet build MinecraftLauncher.sln -c Release

# 2. ADR-001 架构护栏检查（确保 Core 层零 UI / Win32 污染）
dotnet run --project tests/Architecture.Check -c Release

# 3. 核心业务与配置持久化回归（730+ 项，覆盖 AppearanceStore 新旧主题兼容）
dotnet run --project tests/Core.Checks -c Release

# 4. Avalonia Headless 桌面界面全套回归检查
dotnet run --project tests/Desktop.Checks -c Release
dotnet run --project tests/Desktop.Checks -c Release -- --empty
dotnet run --project tests/Desktop.Checks -c Release -- --legacy-theme
dotnet run --project tests/Desktop.Checks -c Release -- --workspace
dotnet run --project tests/Desktop.Checks -c Release -- --multiplayer
```

### 3. `Desktop.Checks` 需同步更新与新增的检查项（见 `samples/M8-ShowcaseAndChecks/UiRedesignChecks.Sample.cs`）
- 更新 `--legacy-theme` 断言：验证历史未知主题平滑回退至默认主题，且 `ThemeChoices` 包含深色与浅色两套预设、支持 `ThemeMode.System/Light/Dark` 切换。
- 新增 `UiV11RedesignChecks`：
  1. 验证侧边栏展开宽度精确为 `240px`，收起后精确为 `72px`，且 `SidebarAppIcon` / `SidebarAppIconCollapsed` 原始草方块图标始终可见。
  2. 验证首页横幅包含固定文案 `"专注于更好的 MC 联机体验"`，且横幅顶部相对于主窗口顶部的 Y 坐标 ≤ `52px`（证实上移 `24–40px` 已生效）。
  3. 验证在 `1440×900` 与 `1366×768` 窗口尺寸下，`LaunchButton`（快速启动）、联机状态卡、实例管理列表与快捷功能区全部位于首屏可视视口内（`Bounds` 不越界）。
  4. 验证连续快速切换 10 次主导航页面 + 连续切换 6 次深浅主题后，`BindingLog.Errors.Count == 0`，且页面最终 `Opacity == 1`。
  5. 验证开启「减少动画」（`MotionMode = "off"`）后窗口挂载 `.reduced-motion` 类，切页与状态反馈不阻塞命令执行。

### 4. Windows 实机人工验收矩阵（交付时须逐项标注实测结果）
- [ ] 无边框窗口拖动、双击标题栏空白最大化/还原、四角与四边拖拽缩放、Win11 最大化按钮悬停分屏布局（Snap Layouts）
- [ ] 100% / 125% / 150% / 200% 系统 DPI 缩放下文字清晰度与图标居中对齐
- [ ] `1366 × 768`、`1440 × 900`、`1920 × 1080` 分辨率下首页首屏无裁切、底部无大块废空白
- [ ] Windows 11 Mica 材质层次与 Windows 10 纯色回退可读性
- [ ] 真实 Minecraft 实例选择、一键启动、取消准备、打开目录、模组管理与联机房间状态同步
