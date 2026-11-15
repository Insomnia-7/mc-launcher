# 方隅-联机 Minecraft 启动器 UI 重构计划与参考样例集（V1.1）

本目录基于仓库 `UI-design` 分支的设计基准文件（`UI-design/UI.md` 与第二版确认参考图 `UI-design/方隅 Minecraft 启动器 UI 设计方案.png`），结合本地工程 `mc-launcher/` 的真实源码审计结果编写。

**实施状态**：本目录最初交付工程审计、M0~M9 计划与参考样例。现已开始按计划修改运行源码；2026-11-15 已补齐 M2 材质适配、M3 标题栏和 M4 侧边栏交互；主页入口随 M6 首页接入。详见 [实施进度与验证范围](02-implementation-progress.md)。

---

## 1. 目录结构索引

```text
UI-design-plan/
├── README.md                                  # 总览说明、UI.md 遵循矩阵与里程碑速览
├── 00-M0-engineering-audit.md                 # M0 工程审计报告（完整覆盖 UI.md §18 的 18 项检查）
├── 01-milestone-plan-M0-to-M9.md              # M0 ~ M9 完整分阶段实施计划、像素预算与验证清单
└── samples/                                   # 适配 .NET 10 + Avalonia 12.1.4 + 编译绑定的参考样例
    ├── M1-DesignSystem/
    │   ├── DesktopThemes.Sample.cs            # 深色(#101114/#8B7CFF) + 浅色(#F7F8FA/#6E60DB)双主题色板定义
    │   └── ThemeTokens.Sample.axaml           # 颜色资源 Key、字体层级、4px 间距与 4/8/12/16 圆角分级
    ├── M2-ThemeAndMica/
    │   └── ThemeModeAndAppearanceManager.Sample.cs # ThemeMode(System/Light/Dark)、系统跟随、持久化与 Mica 降级回退
    ├── M3-WindowAndTitleBar/
    │   └── CustomTitleBar.Sample.axaml        # 无边框窗口装饰角色、原始草方块 Logo、主题快切与窗口控制键
    ├── M4-Sidebar/
    │   ├── Sidebar240To72.Sample.axaml        # 240px ↔ 72px 双态侧边栏、原始品牌标识保留与居中收起态
    │   └── SidebarController.Sample.cs        # 展开/收起文字分步时序动画与单一平滑滑动指示条 NavIndicator
    ├── M5-NavigationAndMotion/
    │   └── MotionTokensAndPageHost.Sample.cs  # UI.md §27 MotionTokens 与双层防闪白 AnimatedContentHost + 交错入场
    ├── M6-HomeLayout/
    │   ├── HomeView.Sample.axaml              # 横幅上移 28-36px 后的首页双栏布局（快速启动/联机状态/实例列表/快捷功能）
    │   └── MainViewModel.Home.Sample.cs       # 复用真实业务数据与命令的首页扩展分部类（严禁伪造 NAT 或假实例）
    ├── M7-Components/
    │   ├── ButtonsDialogsToast.Sample.axaml   # 8 态统一按钮、缩放淡入弹窗、450ms Tooltip 与右下角 Toast 队列视图
    │   └── ToastService.Sample.cs             # 支持 Success/Error/Warning/Info 四态自动消失与手动关闭的通知服务
    └── M8-ShowcaseAndChecks/
        ├── UiShowcaseView.Sample.axaml        # 仅开发模式激活的 UI Showcase 预览页（不进入普通用户正式导航）
        └── UiRedesignChecks.Sample.cs         # 基于 Avalonia.Headless 的 M1~M9 自动化回归验收样例
```

---

## 2. 遵循 `UI.md` 核心约束对照矩阵

| `UI.md` 章节与核心要求 | 工程现状（`mc-launcher/src/Launcher.Desktop`） | 计划落地方案与里程碑归属 |
|---|---|---|
| **§0.1 / §3 视觉配比**：Linear × Arc 主导（65%–75%），Fluent 2 与 Mica/Acrylic 辅助 | 当前为纯黑白灰阶单调色板 + `#4C8DFF` 亮蓝竖条，无材质分层 | **M1 / M2**：切换为低饱和度蓝灰黑 `#101114` / 浅灰白 `#F7F8FA` + 蓝紫强调 `#8B7CFF` / `#6E60DB`，1px 细边框取代重阴影，Win11 启用轻量 Mica 回退 |
| **§0.1 #5 / §12 / §13 / §21 双主题与三态模式**：`System / Light / Dark`，同一设计系统 | `DesktopThemes.cs` 仅保留 `glacier` 日间白一种主题，历史深色被强制迁移为白色 | **M1 / M2**：恢复深浅双主题并新增 `ThemeMode { System, Light, Dark }`，接入 `IPlatformSettings.ColorValuesChanged`，不重建窗口动态过渡 |
| **§0.1 #8 保留原始品牌 Logo**：设计图紫色山峰仅为占位 | 已有 `Assets/Brand/fangyu.png` 与 `fangyu.ico`（草方本源立体方块） | **M3 / M4**：标题栏、侧边栏展开态与 72px 收起态全程保留 `fangyu.png` 与 `fangyu.ico`，绝不替换为占位图标 |
| **§0.1 #7 / §5.1 / §25.1 顶部横幅上移 24–40px**：协调垂直空间，首屏展示更多真实内容 | 现首页 `LibraryView` 顶栏下有 `12px` 页边距 + `78px` 大标题区，首屏实例被挤到下半屏 | **M6**：页标题并入 38px 轻量标题栏面包屑，内容区上边距收紧至 `8px`（相对第二版参考图上移约 `28–32px`），横幅定高 `88px`，1440×900 首屏可展示 8+ 个实例，1366×768 可展示 5+ 个实例 |
| **§2 / §25 业务真实性**：严禁做成商城，严禁删改现有功能，严禁伪造 NAT 或静态假数据 | 具备完整实例管理、启动、Modrinth/CurseForge 下载、角色衣柜、持久任务中心、EasyTier 联机与代理设置；**无独立 STUN NAT 类型探测** | **M4 / M6**：完整保留游戏库/角色/任务中心/联机/下载/设置全部入口；首页联机卡片绑定真实 `Multiplayer` 引擎/房间状态、实测会话 Ping 与 `ActiveNetworkText` 代理/下载源状态，不伪造“NAT 开放” |
| **§6 / §23 侧边栏双态**：`240px` 展开 ↔ `72px` 收起，文字淡入淡出+微移，单一平滑指示条 | 当前展开宽为 `236px`，文字在 `t=0` 瞬时 `IsVisible=False` 无淡出位移，已有单条 `NavIndicator` | **M4**：校准展开宽度为严格 `240px`（收起 `72px`），实现“收起先隐字再缩宽、展开先扩宽再显字”的分步动画与 450ms Tooltip |
| **§15 / §24 / §27 导航与动效系统**：统一 Motion Tokens、页面退场/进场双层过渡、7 段交错入场、减少动画 | `MainWindow.axaml` 单层 `ContentControl` 切页无过渡；`MotionTiming` 仅有 83/167/250/420 四档 | **M5**：对齐 `UI.md §27` 时长表建立 `MotionTokens`，构建双层防闪白 `AnimatedContentHost`（旧页 `0→-4px` 淡出，新页 `8px→0` 淡入）与首屏 7 段 `40ms` 错峰入场，兼容 `reduced-motion` |
| **§26 / §29 / §33 全局组件、预览页与构建验证**：8 态按钮、缩放弹窗、Toast 队列、开发 Showcase、测试区分 | 底部仅有单条 `NoticeReveal` 横条，弹窗无 `Scale 0.97→1` 进退场，无开发预览页 | **M7 / M8 / M9**：补齐按钮 8 态、弹窗缩放遮罩过渡、右下角多条队列 Toast、环境变量门控 `UiShowcaseView`，并通过 `Architecture.Check` / `Core.Checks` / `Desktop.Checks` 验收 |

---

## 3. M0 ~ M9 里程碑速览

| 里程碑 | 阶段名称 | 对应 `UI.md` | 核心交付物 | 风险等级 |
|---|---|---|---|---|
| **M0** | 工程基线审计与契约冻结 | Phase 1 (§18, §19) | 18 项工程审计报告、双前端边界确认、真实业务字段映射表 | 只读无风险 |
| **M1** | 设计系统与双主题色板资源 | Phase 2 (§12–§14, §20) | Linear×Arc 深浅色 Token、字体层级、4px 间距、分级圆角资源字典 | 低（纯资源层） |
| **M2** | 三态主题管理与 Mica 材质适配 | Phase 8 & §21, §28 | `ThemeMode(System/Light/Dark)`、系统主题监听、`appearance.json` 兼容持久化、Win11 Mica 安全回退 | 中（涉及持久化校验） |
| **M3** | 轻量无边框窗口与自定义标题栏 | Phase 3 (§4.2, §22) | 38px 标题栏、原始草方块 Logo、当前页标题、主题快切按钮、窗口控制与最大化边距适配 | 中（需验证原生命中测试） |
| **M4** | 240px / 72px 双态侧边栏与平滑指示条 | Phase 4 (§6, §23) | 严格 `240px ↔ 72px` 过渡、文字分步淡入淡出位移、单条连续滑动 `NavIndicator`、收起态 Tooltip | 中（动画时序协调） |
| **M5** | 统一动效 Token 与页面切换动画容器 | Phase 5 (§15, §24, §27, §30) | `MotionTokens`、`AnimatedContentHost`（旧页 `-4px` 淡出 / 新页 `+8px` 淡入）、7 段交错入场、减少动画适配 | 中高（需防连点闪白） |
| **M6** | 首页空间重算与核心业务模块重构 | Phase 6 (§5, §7–§11, §25) | 横幅上移 `28–36px`（定高 88px）、快速启动卡、真实联机/网络状态卡、紧凑多实例列表、2×3 快捷入口 | 高（核心视觉与业务绑定） |
| **M7** | 全局交互组件系统（按钮/弹窗/Toast/Tooltip） | Phase 7 (§15, §26) | 8 态可复用按钮、`Scale 0.97→1` 模态框过渡、450ms Tooltip、右下角多条有序排列 Toast 通知中心 | 中 |
| **M8** | 开发模式 UI Showcase 预览页 | §29 | 仅 `FANGYU_DEV_SHOWCASE=1` 可见的组件/双主题/动效联调页，不污染正式菜单 | 低（隔离入口） |
| **M9** | 编译构建、自动化检查与 Windows 实机验收 | Phase 9 (§31, §33, §34) | `dotnet build`、`Architecture.Check`、`Core.Checks`、`Desktop.Checks` 更新及 Windows DPI/分辨率实测清单 | 收口验证 |
