# 界面规范参考：5 个热门启动器拆解 → 我们的 WPF 落地

> 目的：给「按钮分布、字号、按钮尺寸、切页动效、配色」找可执行的参照，而不是凭感觉调像素。
> 所有数值都标注了出处。**没有一条是「我记得大概是这样」**。

## 0. 阅读约定与数据来源

| 标记 | 含义 |
|---|---|
| 【源码】 | 从仓库源码里直接读到的常量，后面带 `路径:行号`，可复核 |
| 【实测】 | 我们自己仓库里的现状（`mc-launcher/` 内，带行号） |
| 【推荐】 | 我给的建议值。**没有源码依据**，但后面会给对比度/节奏理由 |
| 【截图估计】 | 从 README 截图目测 —— 本次**一条都没有**（见下） |

**关于截图**：5 个仓库我都拉了文件树并下载了关键源码（缓存见 `scratch/ui-ref/`，共 40 个文件），但**没有逐张比对 README 截图**。所以本文凡是「布局骨架」的描述，都是源码里的容器结构 + 固定尺寸常量，不是看图说话。这比编造「目测约 240px」可靠，也意味着**视觉观感上的细节（阴影浓度、渐变方向）本文覆盖不到**，那部分要开应用自己看。

复核用到的关键文件（`scratch/ui-ref/` 下同名）：

```
prism_DarkTheme.cpp      PrismLauncher 深色调色板
prism_MainWindow.ui      PrismLauncher 主窗口容器结构
prism_MainWindow.cpp     PrismLauncher 右侧工具栏最小宽度
prism_InstanceView.cpp   PrismLauncher 实例列表行高
prism_PageContainer.cpp  PrismLauncher 页面头部内边距
hmcl_root.css            HMCL 主样式（2090 行，尺寸常量都在这）
hmcl_blue.css            HMCL 浅色 Monet 板（深色板不在 CSS 里，见 §3.2）
hmcl_MainPage.java       HMCL 主页面结构（样式类名，无像素常量）
xmcl_vuetify.ts          x-minecraft-launcher 主题与圆角
xmcl_surfaceTokens.ts    x-minecraft-launcher 圆角/阴影/过渡 token
xmcl_common.css          x-minecraft-launcher 全局过渡曲线
xmcl_SideBarClassic.vue  窄栏宽度与条目容器
xmcl_SideBarItem.vue     导航条目尺寸与指示器
axo_variables.scss       Axolotl 色阶/间距/圆角/hover 亮度
axo_global.scss          Axolotl 页面切换过渡
axo_ButtonFrame.vue      Axolotl 按钮尺寸体系
axo_NavRail.vue          Axolotl 导航栏滑块动效
axo_NavButton.vue        Axolotl 导航按钮尺寸
axo_window-frame.css     Axolotl 窗口圆角与描边
pb_ProgressValue.cs      ProjBobcat 进度模型
pb_ProgressReportBase.cs ProjBobcat 阶段事件
pb_TaskResultStatus.cs   ProjBobcat 结果五态
```

---

## 1. 布局骨架对比

### 1.1 HMCL（JavaFX）

**结构**：左侧栏（版本选择 + 版本列表）+ 右侧内容区，右下角一个**固定尺寸的大启动按钮**。

- 启动按钮区 `.launch-pane` 被写死成 `230 × 57px`【源码】`hmcl_root.css:429-434`；
  里面是两个按钮拼的：主按钮 `.launch-button` `200 × 55px`【源码】`hmcl_root.css:445-459`，
  右边挂一个 `27px` 宽的下拉菜单按钮 `.menu-button`【源码】`hmcl_root.css:461-466`。
- 主按钮圆角 `4px 0 0 4px`、菜单按钮 `0 4px 4px 0`——**两个按钮共用一组圆角**，视觉上是一颗胶囊被切开【源码】同上。
- 主按钮文字 `14px`【源码】`hmcl_root.css:474-477`；按钮底色取 `-monet-primary-container`，文字取 `-monet-on-primary-container`【源码】`hmcl_root.css:440,475`。
- 列表项高度有三档：`40px`【源码】`hmcl_root.css:676-678,698-700,776`、`42px`【源码】`:733`、`50px`【源码】`:1248`；还有 `36px`【源码】`:1546`。
- **侧栏宽度没有硬编码**：`MainPage.java` 里通篇是 `getStyleClass().add(...)`，宽度交给 JavaFX 布局与 CSS 权重【源码】`hmcl_MainPage.java`（全文 21.5KB，grep 无 `setPrefWidth`）。

**结论**：HMCL 最值钱的一招是「大按钮 + 附带下拉菜单」，把「启动」和「启动变体（离线/测试）」塞进同一个视觉块；侧栏宽度不写死，窗口缩放更自然。

### 1.2 PrismLauncher（Qt Widgets）

**结构**：顶工具栏 + 中央实例网格 + 右侧详情工具栏 + 底部新闻条 + 状态栏。

- 默认窗口 `800 × 600`【源码】`prism_MainWindow.ui`（QMainWindow 的 geometry）。
- 中央区域是一个 `QHBoxLayout`，`contentsMargins` 全 0、`spacing` 0【源码】`prism_MainWindow.ui` —— **所有分栏由 toolbar 承担**，不是自己摆的 Splitter。
- 顶栏 `mainToolBar` 用 `ToolButtonTextBesideIcon`（图标 + 文字并排）；底部 `newsToolBar` 图标 `16 × 16`；右侧 `instanceToolBar` 也在 `RightToolBarArea`，图标 `16 × 16`【源码】`prism_MainWindow.ui`。
- 右侧详情栏最小宽度 `140px`【源码】`prism_MainWindow.cpp:164`：`ui->instanceToolBar->setMinimumWidth(140);`
- **实例列表行高 64px**：`itemScroll = 64;`【源码】`prism_InstanceView.cpp:166`（另一条分支按分类内容高度均分）。
- 卡片按网格算位置：`out.setTop(cat->verticalPosition() + cat->headerHeight() + 5 + cat->rowTopOf(index));`、`out.setLeft(m_spacing + x * (itemWidth() + m_spacing));`【源码】`prism_InstanceView.cpp:724-725`。
- 页面头部只留 6px 上边距：`headerHLayout->setContentsMargins(0, 6, 0, 0);`【源码】`prism_PageContainer.cpp:180`。

**结论**：三件事值得抄 —— 行高 64、内容区顶部只留 6px（信息密度）、右侧详情栏「最小 140px 而不是固定 240」。

### 1.3 x-minecraft-launcher（Vue / Electron / Vuetify）

**结构**：80px 图标窄栏 + 主内容区，页切换用淡入淡出，卡片式排版。

- 侧栏 `min-width: 80px; width: 80px; height: 100%`【源码】`xmcl_SideBarClassic.vue:323-324`；条目区 `padding: 8px 0` 在 `:331`。
- 导航条目 `48 × 48`、圆形，`margin: 4px auto`【源码】`xmcl_SideBarItem.vue:52-57`；
  hover/active 时圆角从 `50%` 变成 `16px`（方形化），背景换成条目自己的颜色【源码】`xmcl_SideBarItem.vue:88-101`。
- 左侧有个 **Discord 风格竖条指示器**：`width: 4px`，平时 `height: 0`，hover `20px`，active `36px`，`left: -12px`【源码】`xmcl_SideBarItem.vue:112-137`。
- 菜单项 `min-height: 40px`【源码】`xmcl_common.css:247`。
- 圆角体系：面板 16 / 对话框 32 / 菜单项 10 / 卡片 16 / 子区 12 / 条目 10 / 按钮 `'lg'`【源码】`xmcl_surfaceTokens.ts`。
- 页切换 `<transition name="fade-transition" mode="out-in">`【源码】`xmcl_App.vue:18`（`out-in` = 先出后进，不会两页叠在一起）。

**结论**：窄栏（80px）+ 图标 + pill 指示器省地方又好看，但**不适合我们**——我们只有两个页签却要显示版本列表，图标栏装不下文字信息。可借的是「`out-in` 切换」和「圆角分级」。

### 1.4 Axolotl（Nuxt / Vue / Tauri）

**结构**：顶栏 + 左侧图标栏 + 右侧抽屉 + 主内容，四段式。

- 三个写死的布局常量：`--top-bar-height: 3rem`（48px）、`--left-bar-width: 4rem`（64px）、`--right-bar-width: 300px`【源码】`axo_App.vue:3568-3570`；代码里另有 `const APP_SIDEBAR_WIDTH = 300`【源码】`axo_App.vue:258`。
- 按钮尺寸是一套完整刻度：`2xs 24 / xs 28 / sm 32 / md 36 / lg 40 / xl 48`（px），圆角对应 `8 / 8 / 10 / 12 / 14 / 16`，左右内边距 `2xs~sm` 为 `px-1.5`、`md` 为 `px-2.5`、`lg` 为 `px-4`、`xl` 为 `px-3.5`【源码】`axo_ButtonFrame.vue:32-37`。
- 图标尺寸随按钮走：`sm` 及以下 16px、`md/lg` 20px、`xl` 24px【源码】同上。
- 导航按钮 `w-12 h-12`（48×48）圆形，图标 `text-2xl`（24px）【源码】`axo_NavButton.vue:7,19,31`。
- 窗口非最大化时靠 **1px inset 描边**做边框：`box-shadow: inset 0 0 0 1px var(--surface-5)` + `border-radius: inherit`【源码】`axo_window-frame.css:1-8`；自定义背景时窗口圆角 `8px` + `clip-path: inset(0 round 8px)`【源码】`axo_App.vue:3640`。

**结论**：**尺寸刻度体系**（按钮 6 档、圆角 6 档、间距 5 档）和**1px inset 描边做窗口边框**最值得抄——后者正好解决我们「无边框窗口 + 深色」在浅色桌面上边界发虚的问题。

### 1.5 ProjBobcat（C# 核心库，无界面）

它没有 UI，但**进度/状态模型**是所有启动器界面都要对上的东西：

- `ProgressValue` 是 `readonly record struct`，内部存 `NormalizedValue`（0~1），对外暴露 `DisplayValue => NormalizedValue * 100`，并内置 `Start`/`Finished` 两个常量【源码】`pb_ProgressValue.cs`。
- 进度上报走事件：`StageChangedEventDelegate(StageChangedEventArgs { CurrentStage, Progress })`【源码】`pb_ProgressReportBase.cs` —— **阶段名和进度值绑在一起发**，界面才能同时显示「正在下载库文件 63%」。
- 结果状态五态：`Unknown / Error / Success / Warning / PartialSuccess`【源码】`pb_TaskResultStatus.cs`。

**结论**：我们的进度区现在只有一根 `ProgressBar` + 一行文字（`MainWindow.xaml:278-286`），缺「阶段名」和「五态结果」。装完 72 个文件后到底是全成功还是部分失败，界面上分不出来。

### 1.6 结论：我们该采用哪种骨架

**采用「Axolotl 三段式 + Prism 的信息密度 + HMCL 的主操作块」**：

```
┌────────────────────────────────────────────────────────┐
│ 顶栏 48px（可拖拽区 + 窗口按钮 + 全局状态）             │
├──────────────┬─────────────────────────────────────────┤
│ 左栏 240px   │ 内容区（MaxWidth 1120 居中，页边距 32,24）│
│ （保留现状） │  ├ H1 24 + 副标题 12 muted              │
│              │  ├ 主操作 52px（启动）                  │
│              │  ├ 次操作行 36px（导入 / 导出 / …）      │
│              │  ├ 状态卡（进度 + 阶段名 + 速度 + 结果） │
│              │  └ 设置区（可折叠）                     │
└──────────────┴─────────────────────────────────────────┘
```

理由：
1. 左栏 240px 已经够放「版本列表 + 两个页签」，改成 80px 图标栏反而塞不下版本名；改成 300px 会挤压内容区（我们最小宽度 820，300 + 32×2 只剩 456 给内容）。
2. 顶栏 48px 是 Axolotl 的实测值，比 Prism 的工具栏更省，也正好覆盖我们要塞的「拖拽区 + 三个窗口按钮」。
3. 内容区**必须加 MaxWidth**：现在窗口拉到 1920 时正文被拉成一条 1700px 的长行（`MainWindow.xaml:172` 只有 Margin 没有上限），阅读体验很差。

---

## 2. 尺寸与字体

### 2.1 对照表

| 项目 | HMCL | Prism | x-minecraft-launcher | Axolotl | 我们现状【实测】 | 推荐【推荐】 |
|---|---|---|---|---|---|---|
| 顶栏高 | 无独立顶栏（标题栏自绘） | 工具栏自适应（图标 16） | 系统栏 `headerHeight + 70` 渐变 | **48** `3rem` | 无（原生标题栏） | **48** |
| 左栏宽 | 不写死（布局权重） | 右侧详情栏 **min 140** | **80** 图标窄栏 | **64** 图标窄栏 | **240** `MainWindow.xaml:20` | 240（保留） |
| 导航项高 | 40 / 42 / 50 | —（工具栏按钮） | **48** 方块 | **48** 方块 | 页签按钮 **34** `:39,53`；`NavItem` 44 定义了但**没人用** `App.xaml:192` | 页签 36 / 列表项 60 |
| 列表行高 | 40 / 42 / 50 | **64** | 48 + 4 外边距 | — | **56** `App.xaml:164` | **60** |
| 主按钮高 | **55**（宽 200） | — | — | **48**（xl） | **52** `App.xaml:102` | **52**（保留） |
| 次按钮高 | — | — | 40（菜单项） | **36**（md）/ 40（lg） | **40** `App.xaml:138` | **36** |
| 图标按钮 | 27（下拉） | 16 图标 | 48 方块 | 48 方块 | 26-36 不等 | 32 |
| 正文字号 | 14 | 系统默认 | 14（`text-sm`） | 14（`text-sm`） | **14** `App.xaml:34` | 14 |
| 标题字号 | 20 / 16 | — | — | — | **26**（H1）`App.xaml:45` | 24（H1）/ 16（H2） |
| 说明文字 | 12 / 13 | — | 12 | 12 | **12**（Caption）`App.xaml:40` | 12 |
| 卡片圆角 | 4 / 5 | — | **16**（卡片）/ 12（子区） | 16（`radius-lg`） | **12** `App.xaml:26` | 12（保留） |
| 按钮圆角 | 4 | — | 10 | 10~16 | 主 10 / 次 8 `App.xaml:110,145` | 主 12 / 次 10 |
| 输入框 | — | — | — | 32 高（md） | **30** 高 / 圆角 4 `App.xaml:217,232` | 32 / 圆角 6 |
| 间距刻度 | 10 / 16 | 6 / 8 | 6 / 8 / 12 / 16 | 4 / 8 / 12 / 16 / 24（`.25/.5/.75/1/1.5rem`） | 2 / 6 / 8 / 12 / 14 / 16 / 22（散落） | 4 / 8 / 12 / 16 / 24 五档 |
| 内容页边距 | — | 头部 `(0, 6, 0, 0)` | — | — | **32,28,32,24** `MainWindow.xaml:172`；`PagePadding` 32,26,32,20 定义了但**没人用** `App.xaml:27` | 32,24,32,24 |
| 字体族 | 系统 | 系统 | 系统 | 系统 | **Microsoft YaHei UI** `App.xaml:33` | 保留（中文首选） |

### 2.2 从表里看出来的三件事

1. **我们的字号是「没有阶梯」的**：14 / 12 / 15 / 17 / 26 五个值来自五个不同地方，其中 15 和 17 只差 2px，视觉上分不出层级。Axolotl/HMCL 都是「12 / 14 / 16 / 20」四档跳。
2. **按钮高度反而比主流大**：我们主按钮 52、次按钮 40，而 Axolotl 最大档 48、次档 36。52 的启动按钮不算错（HMCL 55、我们单主操作），但次按钮 40 偏大，一排三个就显拥挤。
3. **定义好了却没人用的样式**：`NavItem`（44px，`App.xaml:192`）、`PagePadding`（`App.xaml:27`）都是死代码。要么接线，要么删——留着会让后来改界面的人以为改它有用。

---

## 3. 配色

### 3.1 PrismLauncher（深色，全部【源码】`prism_DarkTheme.cpp`）

| 角色 | 值 |
|---|---|
| Window（窗口底） | `#313131` |
| Base（输入/列表底） | `#222222` |
| AlternateBase（交替行） | `#2A2A2A` |
| Button | `#303030` |
| Text | `#FFFFFF` |
| Highlight（选中） | `#96DB59`（亮绿） |
| HighlightedText | `#000000` |
| Link | `#2FA3C6` |
| ToolTip | 底 `#2A82DA`、字 `#FFFFFF`、1px 白边 |
| 淡出 | `fadeAmount 0.5`，`fadeColor #313131` |

特点：**整块灰阶几乎不动**（313131 / 303030 / 2A2A2A / 222222 差 1~3%），层次完全靠 `#96DB59` 一个高饱和强调色 + 1px 描边撑起来。

### 3.2 HMCL（Monet 动态取色）

- 浅色板【源码】`hmcl_blue.css`：`primary #4352A5`、`primary-container #5C6BC0`、`surface #FBF8FF`、`on-surface #1B1B21`、`surface-container #EFEDF5`、`outline-variant #C6C5D3`、种子色 `#5C6BC0`；半透明变体 `#4352A580`（50%）/ `#4352A5CC`（80%）。
- **深色板不在 CSS 里**：`hmcl_dark.css` 只有 34 行，覆盖的是日志窗口（底 `#282828`、warn `#413c26`/`#fdf3aa`、error `#4e3534`/`#f9dedc`、fatal `#842029`）和附加包通道图标色（alpha `#ff496e` / beta `#ffa347` / release `#1bd96a`）。真正的深色 `-monet-*` 由 Java 端从种子色生成（`HMCL/src/main/java/org/jackhuang/hmcl/theme/`）。**这条我标「未取到源码」**，不猜数值。

特点：从一张壁纸里抽种子色，整套界面自动协调——这是「配色和谐」最彻底的解法，但需要一套 HCT/Monet 生成算法，我们短期内做不了。

### 3.3 x-minecraft-launcher

- Vuetify 深色主题：`primary #4caf50`、`accent #00e676`【源码】`xmcl_vuetify.ts`。
- 面板不是纯色，是 **半透明 + 模糊**：`DEFAULT_SURFACE_BG = rgba(var(--v-theme-surface), 0.82)`、`DEFAULT_SURFACE_BLUR = 48`【源码】`xmcl_surfaceTokens.ts`。
- 描边 `1px solid rgba(var(--v-theme-on-surface), 0.10)`；阴影 `0 12px 40px -8px rgba(0,0,0,0.35)`【源码】同上。
- 卡片按下缩放 `0.98`【源码】同上。

特点：绿系强调色 + 玻璃拟态。**我们不适合**——WPF 里做实时背景模糊要么用 `BlurEffect`（每帧重绘，1080p 下明显掉帧）要么用 Win32 `SetWindowCompositionAttribute`（不稳定）。

### 3.4 Axolotl（深色阶做得最细）

- 深色 surface 六级【源码】`axo_variables.scss`：
  `#101215` → `#15181c` → `#1a1e24` → `#20242b` → `#262b34` → `#333a46` → `#424854`
  （源码注释说明：每级之间至少 ~1.12:1 对比，否则卡片会糊在一起）。
- OLED 变体把 surface-1 压到 `#000000`。
- hover 靠亮度乘数：浅色 `--hover-brightness: 0.9`、深色 `1.25`。
- 品牌色族：`blue-500 #2b79cc`、`green-500 #09de78`、`red-500 #ed4661`；焦点环 `#ea80ff`（默认）或 `var(--color-brand)`。
- 间距 `.25/.5/.75/1/1.5rem`；圆角 `.25/.5/.75/1/1.25rem`【源码】`axo_variables.scss:2030-2041`。

### 3.5 我们现状的问题（用对比度量化）

| 关系 | 现状值 | 对比度 | 判断 |
|---|---|---|---|
| 正文 `#E9EBF2` / 背景 `#121317` | — | **15.58:1** | 好 |
| 次要 `#9BA2B2` / 背景 | — | **7.25:1** | 好 |
| 主色 `#5B8DEF` / 背景 | — | **5.75:1** | 好（图标/链接可用） |
| **白字 / 主按钮 `#5B8DEF`** | — | **3.23:1** | **不达标**（AA 正文需 4.5:1） |
| **面板 `#181A20` / 背景 `#121317`** | — | **1.07:1** | **侧栏几乎和背景糊在一起** |
| 卡片 `#1F222A` / 背景 | — | 1.17:1 | 偏弱但可接受 |
| 描边 `#2C3038` / 卡片 `#1F222A` | — | **1.20:1** | **描边基本看不见** |

这就是「配色不和谐」的实际来源：不是色相问题，是**层次差太小 + 主按钮白字对比不够**。

### 3.6 推荐色板【推荐】

保留 `#5B8DEF` 作为品牌色（已经出现在 `DropShadowEffect` 和选中态里），但**拆成两级用法**：

| Token | 值 | 用途 | 对比度 |
|---|---|---|---|
| `BgBrush` | `#0C0E12` | 窗口底 | — |
| `PanelBrush` | `#15181D` | 左栏 / 顶栏 | 对 bg **1.09:1** |
| `CardBrush` | `#1C2028` | 卡片 | 对 bg **1.18:1** |
| `FieldBrush` | `#252A33` | 输入框 / 进度槽 | 对卡片 1.13:1 |
| `HoverBrush` | `#2E3440` | 悬停 | 对卡片 1.15:1 |
| `LineBrush` | `#3E4552` | 描边（**加强**） | 对卡片 **1.69:1** |
| `TextBrush` | `#E8EBF2` | 正文 | 对 bg **16.19:1** |
| `MutedBrush` | `#A8B0C0` | 次要文字 | 对 bg **8.86:1** |
| `FaintBrush`（新） | `#78808F` | 三级文字/占位符 | 对卡片 **4.11:1** |
| `AccentBrush` | `#5B8DEF` | 文字/图标/高亮描边 | 对 bg **5.98:1** |
| `AccentFillBrush`（新） | `#4370CC` | **实心按钮填充** | 白字 **4.74:1** ✓ |
| `AccentPressedBrush`（新） | `#3B67C4` | 按下 | 白字 5.35:1 |
| `SuccessBrush` | `#4CAF7D` | 成功 | 对 bg 7.12:1 |
| `WarnBrush` | `#E0A54A` | 警告 | 对 bg 8.88:1 |
| `DangerBrush` | `#E0574F` | 危险 | 对 bg 5.19:1 |

三条关键决定：

1. **实心按钮不能再用 `#5B8DEF` 配白字**（3.23:1）。降到 `#4370CC` 后 4.74:1 达标，色相不变，肉眼几乎看不出差别。
2. **hover 不能靠「提亮填充」**：`#4E7FE0` 配白字只有 3.86:1，一 hover 反而更难读。改用「1px 内高光描边 `rgba(255,255,255,0.16)` + 阴影加深」表达悬停。
3. **侧栏与背景的分离靠描边，不靠亮度差**：`LineBrush` 提到 `#3E4552`（对卡片 1.69:1），左栏右侧加 1px 竖线——这正是 Axolotl（`inset 0 0 0 1px var(--surface-5)`）和 Prism（整块灰阶只差 1~3% 但描边清楚）的做法。

---

## 4. 动效

### 4.1 源码里的真实数值

| 来源 | 动效 | 时长 | 缓动 |
|---|---|---|---|
| xmcl【源码】`xmcl_common.css:42,49` | 列表增删/移动 | **500ms** | `cubic-bezier(0.55, 0, 0.1, 1)` |
| xmcl【源码】`xmcl_common.css:247-248` | 菜单项背景 | **150ms** | `ease` |
| xmcl【源码】`xmcl_surfaceTokens.ts` | 卡片行（描边/底/位移） | 200 / 150 / 100ms | `ease` |
| xmcl【源码】`xmcl_SideBarItem.vue:88-101` | 导航圆角 + 底色 | **200ms** | `cubic-bezier(0.4, 0, 0.2, 1)` |
| xmcl【源码】`xmcl_SideBarItem.vue:112-137` | 左侧 pill 指示器 | **200ms** | 同上 |
| xmcl【源码】`xmcl_surfaceTokens.ts` | 卡片按下 | 缩放 0.98 | — |
| Axolotl【源码】`axo_global.scss:766-787` | **页切入** | **180ms** | `ease`（只改 opacity） |
| Axolotl【源码】同上 | **页切出** | **120ms** | `ease` |
| Axolotl【源码】`axo_ButtonFrame.vue` | 按钮颜色/缩放 | **150ms** | `ease-out`，按下 `scale(0.97)` |
| Axolotl【源码】`axo_App.vue:3721` | 右侧抽屉 | **320ms** | `cubic-bezier(0.22, 1, 0.36, 1)`（强 ease-out） |
| Axolotl【源码】`axo_NavRail.vue` | 导航滑块位置 | **150ms** | `cubic-bezier(0.4, 0, 0.2, 1)` |
| Axolotl【源码】同上 | 滑块透明度 | 250ms（延迟 50ms） | `cubic-bezier(0.5, 0, 0.2, 1)` |
| Axolotl【源码】`axo_NavRail.vue` | 入场错峰 | **120ms/项** | — |
| HMCL / Prism | 无 CSS 动效 | — | JavaFX/Qt 各自少量属性动画 |

规律很清楚：**页切换 120~180ms、按钮反馈 150ms、抽屉 320ms、列表错峰 120ms**，缓动几乎都是「快出慢收」的 ease-out 系。

### 4.2 推荐参数【推荐】

| 场景 | 时长 | 缓动 | 说明 |
|---|---|---|---|
| 页切入（游戏版本 ↔ 下载） | **200ms** | CubicEase EaseOut | 比 Axolotl 的 180ms 略长，因为我们还带 10px 上移 |
| 页切出 | **100ms** | EaseOut | 只淡出，不做位移，避免「两页同时动」 |
| 按钮按下 | **120ms** | EaseOut | 缩放 0.97（Axolotl 同值） |
| 按钮悬停（描边/底色） | **150ms** | EaseOut | — |
| 列表项入场错峰 | **180ms** 基础 + **40ms/项**，最多 5 项 | EaseOut | Axolotl 用 120ms/项，10 项就要 1.2s，太长 |
| 进度条数值变化 | **200ms** | EaseOut | 数字跳变会显得卡 |
| 折叠区展开 | **250ms** | EaseOut | — |
| 抽屉（若做右侧详情） | **300ms** | `cubic-bezier(0.22,1,0.36,1)` ≈ `QuarticEase EaseOut` | — |

### 4.3 WPF 落地方式

三种手段，按「该用哪个」排：

| 方式 | 适用 | 代价 |
|---|---|---|
| **附加属性 + `BeginAnimation`**（我们已有） | 固定的一次性动画：切页、按压、入场 | 代码量中等，XAML 一行声明 |
| **Storyboard（XAML）** | 需要设计器可见、或要 `VisualStateManager` 联动 | 模板里写起来啰嗦，改参数要动 XAML |
| **`CompositionTarget.Rendering`** | 逐帧连续变化（下载速度表、粒子） | 每帧回调，必须自己控帧率；滥用会掉帧 |

我们仓库里**已经有三个原语**（提交 `9c66a2d`）：

- `src/Launcher.App/Animations/PageTransition.cs`
  切页淡入 + 上移：`DefaultDurationMs = 200`（:16）、`DefaultOffsetY = 10`（:17）、`CubicEase EaseOut`（`MotionCore.EaseOut()`，:216）；
  两种用法：代码 `PageTransition.Play(this)`，或 XAML `anim:PageTransition.AutoPlay="True"`（:23-27）。
- `src/Launcher.App/Animations/PressFeedback.cs`
  按下 `PressDurationMs = 120`（:20）、悬停 `HoverDurationMs = 150`（:21），附加属性 `Scale` / `HoverLift`。
- `src/Launcher.App/Animations/RevealPanel.cs`
  列表入场 `DefaultDurationMs = 180`（:21）。
- 三者共用 `MotionCore`（在 `PageTransition.cs:171`）：只往 `TransformGroup` 里追加自己的 `ScaleTransform`/`TranslateTransform`，**不覆盖模板原有变换**，并且把 `RenderTransformOrigin` 设成 `(0.5, 0.5)`（:205），否则按压缩放会往右下角缩。

**但 `grep -rn "anim:" --include=*.xaml` 在整个 `src/Launcher.App/` 里零命中**——原语写好了，一行都没接。落地清单第 8、9 条就是这件事。

---

## 5. 可借鉴 vs 别学

### HMCL

- ✅ **大按钮 + 下拉菜单拼成一颗胶囊**：`200+27 = 227px` 宽、共用圆角（`4px 0 0 4px` / `0 4px 4px 0`）。我们的「启动游戏」52px 高但右边什么都没有，可以挂一个「启动选项 ▾」。
- ✅ **主按钮文字 14px 而不是 17px**：按钮大、字不大，视觉上更稳。
- ❌ **不要学 Monet 动态取色**：需要 HCT 色空间 + 种子色生成算法，投入产出比极低。
- ❌ 列表行高三档混用（40/42/50），没有统一刻度，改起来找不到规律。

### PrismLauncher

- ✅ **实例列表行高 64px**：比我们的 56 更舒展，图标 + 两行文字放得下。
- ✅ **内容区顶部只留 6px**：信息密度高，一屏能多看两行。
- ✅ **右侧详情栏用 min-width 而不是固定宽**：`setMinimumWidth(140)`，用户可拖。
- ❌ **纯灰阶 + 单一强调色**（`#96DB59`）太硬，中文界面用亮绿当选中色会很跳。
- ❌ 分栏全靠 toolbar 承载，逻辑分散在 UI 文件和 toolbar 代码两处，不好维护。

### x-minecraft-launcher

- ✅ **页切换用 `out-in`**：先出后进，绝不出现两页叠影。WPF 里对应「旧页 Visibility 先 Collapsed，再播新页动画」。
- ✅ **圆角分级**（面板 16 / 卡片 16 / 子区 12 / 条目 10 / 对话框 32）：层级靠圆角也能表达。
- ✅ **Discord 风格 pill 指示器**（4px 宽、hover 20px / active 36px）：我们如果做竖排导航可以直接用。
- ❌ **玻璃拟态**（`rgba(surface, 0.82)` + `blur 48`）：WPF 里代价高、易掉帧。
- ❌ 500ms 的列表移动动画：拖拽排序时会有「跟不上手」的黏滞感。

### Axolotl

- ✅ **按钮 6 档刻度**（24/28/32/36/40/48）+ 圆角 6 档 + 间距 5 档：直接照搬成我们的 `ButtonHeight` / `Radius` / `Spacing` 资源。
- ✅ **1px inset 描边做无边框窗口的边界**：`inset 0 0 0 1px var(--surface-5)`，比 `WindowChrome` 自带的边框可控。
- ✅ **深色 surface 每级 ≥1.12:1**：这是「卡片不糊」的硬指标，我们照这个校准。
- ❌ 顶栏 48px + 左栏 64px + 右栏 300px 三段全固定：窗口一缩就挤，我们最小宽度只有 820。

### ProjBobcat

- ✅ **`ProgressValue` 归一化 0~1 + `DisplayValue ×100`**：内部用 0~1，只在显示层乘 100，避免各处混用百分比。
- ✅ **阶段名与进度一起上报**（`StageChangedEventArgs { CurrentStage, Progress }`）：界面能显示「正在校验 63%」而不是光秃秃的 63%。
- ✅ **结果五态**（含 `PartialSuccess`）：装 72 个文件挂 3 个，应该显示「部分成功」而不是二选一。
- ❌ 它是核心库不是界面，**布局/视觉一条都别参考**。

---

## 6. 落地清单（按顺序，都能对应到 MainWindow.xaml 的具体区域）

> 顺序原则：先改「资源层」（一处改动全局生效），再改「结构层」（动布局），最后改「动效层」（风险最高、要跑起来看）。

1. **`App.xaml` 换色板** —— 把 §3.6 的 15 个 brush 写进 `App.xaml:8-22`。同步改 `LineBrush`（`#2C3038 → #3E4552`），新增 `FaintBrush` / `AccentFillBrush` / `AccentPressedBrush`。**这一步单独提交、单独看效果**，因为它会改全应用每一处。
2. **`PrimaryButton` 用 `AccentFillBrush` 填充 + 白字**（`App.xaml:98-133`）—— 解决 3.23:1 不达标。悬停改「1px 内高光描边 + 阴影加深」，不要提亮填充。
3. **左栏加 1px 右描边**（`MainWindow.xaml:29` 的 `<Border Background="{StaticResource PanelBrush}">` 加 `BorderBrush="{StaticResource LineBrush}" BorderThickness="0,0,1,0"`）—— 侧栏与背景 1.09:1 的亮度差不够，靠线分开。
4. **主/次按钮尺寸与圆角归一**（`App.xaml:102,110` / `:138,145`）：主 52/圆角 12/字号 16；次 36/圆角 10/字号 13。次按钮从 40 降到 36，一排三个不再挤。
5. **列表行高 56 → 60**（`App.xaml:164` 的 `InstanceItem`），条目内 `Margin` 从 `2,2` 提到 `3,3`，行间有呼吸。
6. **内容区加宽度上限**（`MainWindow.xaml:169-172`）：`ScrollViewer` 内层 `Grid` 加 `MaxWidth="1120" HorizontalAlignment="Center"`，`Margin` 改 `32,24,32,24`。顺手把没人用的 `PagePadding`（`App.xaml:27`）删掉或接线。
7. **两个页签按钮 34 → 36、圆角 6 → 8**（`MainWindow.xaml:39,53`）；把死样式 `NavItem`（`App.xaml:192`）删掉，避免误用。
8. **接 `PageTransition`**：给「游戏版本」和「下载」两个页根元素加 `anim:PageTransition.AutoPlay="True"`（命名空间 `xmlns:anim="clr-namespace:Launcher.App.Animations"`）。切页时先 Collapsed 旧页再切新页，`out-in` 效果。
9. **接 `PressFeedback`**：在 `PrimaryButton` / `SecondaryButton` 模板的 `Border` 上挂 `anim:PressFeedback.Scale="0.97"`，`GhostButton` 挂 `0.96`。
10. **进度区加「阶段名 + 速度 + 五态」**（`MainWindow.xaml:270-300`）：参考 ProjBobcat —— 进度条上方一行 `阶段文案`（12px muted）+ 右侧 `速度/剩余`，结束时按 `TaskResultStatus` 显示成功/警告/部分成功三色（用 `SuccessBrush` / `WarnBrush` / `DangerBrush`）。
11. **输入框 30 → 32、圆角 4 → 6**（`App.xaml:217,232`；`DarkComboBox` 同步 `:251,260`）—— 和按钮的圆角阶梯对齐。
12. **顶栏（若做无边框）**：48px 高、`-1,0,0,0` 起、右侧三个 46×32 窗口按钮；窗口描边用 `inset 0 0 0 1px` 的 Border 覆盖整个 `RootGrid`（Axolotl 做法），`WindowChrome` 的 `GlassFrameThickness` 设 0。

---

## 附：没做的事

- **没有逐张看 README 截图**，所以「哪个更好看」这类判断没有视觉依据，只有数值依据。
- **HMCL 深色板未取到源码**（Java 端生成），§3.2 只给了浅色板。
- **动效没在真机上验证过**：§4.2 的推荐值是按源码区间推的，实际手感要跑起来调（尤其 200ms + 10px 位移在 60Hz 下是 12 帧，偏快）。
