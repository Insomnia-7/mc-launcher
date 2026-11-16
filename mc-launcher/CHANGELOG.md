# 方隅版本记录

## 1.0.0-preview.23 · 启动就绪与导航高亮修复

2026-11-17。预览版；[Release](https://github.com/Insomnia-7/mc-launcher/releases/tag/desktop-v1.0.0-preview.23)。

- 正版头像、用户壁纸和导航布局提前到启动图标阶段准备，准备完成后再显示主窗口并播放原有分区入场动画；图标最短停留 650ms。
- 头像请求在后台执行，最长等待 8 秒；超时或服务不可用时使用账号首字母，不阻止进入启动器。关闭启动图标会取消请求。
- 导航高亮按布局坐标定位，忽略按钮的临时入场位移，修复主页高亮被裁短后突然伸长的现象。
- 启动图标保持透明、主界面保持实色；关闭动画时仍完成启动准备，已有动效速度与单文件精简配置保留。

## 1.0.0-preview.22 · 精简单文件体积

2026-11-16。预览版；[Release](https://github.com/Insomnia-7/mc-launcher/releases/tag/desktop-v1.0.0-preview.22)。

[源码 eda351a](https://github.com/Insomnia-7/mc-launcher/commit/eda351aa12f313ece85db939f3e584d12cb43fd5)。Windows单文件25735418字节，SHA-256：53cbd455fc4255a4a6db913deb897396d3065092bfb6349b9906a4c384df21c8。1699项检查、架构护栏、裁剪程序集、单文件启动及生产更新验签通过。

- 发布包排除已无运行时引用的旧背景图，源码素材保留；当前首页横幅、品牌图标和加载器图标不变。
- Windows 精简配置仅对 BouncyCastle 与 Avalonia 设计器支持库追加按引用裁剪，保留 Ed25519 校验及全部业务模型、JSON/DI 成员。
- 下载器沿用 Parallel.ForEachAsync 的并发上限，移除重复的信号量等待与释放；保持正整数参数约束、取消、断点续传、校验与重试行为。
- 启动和切页动画、中文与字体支持、渲染库及无需另装 .NET 的单文件使用方式保持不变。

体积组成、测量方法与验证范围见 docs/desktop-size-audit.md。

## 1.0.0-preview.21 · 透明启动标识与分区入场

2026-11-16。预览版；[Release](https://github.com/Insomnia-7/mc-launcher/releases/tag/desktop-v1.0.0-preview.21)。

[源码 74149f2](https://github.com/Insomnia-7/mc-launcher/commit/74149f21a8cb921cb00265bc9702583f9c8a9977)。Windows单文件31739103字节，SHA-256：74155b242686a532383cf892313de7dd991bd0e6c533a98ec9c84f2e067fc704。1699项检查、架构护栏、裁剪程序集、单文件启动及生产更新验签通过。

- 修复透明启动标识的文字黑边与彩边，采用纯白灰度抗锯齿，不添加描边或阴影。
- 接入确认后的启动动效：仅图标周围透明；主界面保持实色。左侧七个导航和主页区块以单项800ms、错开80ms依次舒展。
- 页面切换保留各自布局，修复旧页闪现与尺寸跳变；支持快速改选、缩放、最小化和关闭动画后的归位。
- 资源分类中文显示，按类型、功能、分辨率与性能区分；游戏本体增加版本搜索，愚人节版本独立分组并提供简介；分组标题栏统一铺满。
- 修复返回缓存资源页后搜索进度条停止，以及分类清单晚到导致结果被误清空或重复搜索；保留滚轮与连续加载。
- CurseForge 搜索仍需可用的项目 Key；本版不内置此前被拒绝的凭据。Java/Rust 现有能力保留。

验收见 docs/startup-acceptance.md 与 docs/catalog-ui-regression.md。

## 1.0.0-preview.20 · 资源滚动、检索响应与卡片淡入淡出

2026-11-16。预览版；[Release](https://github.com/Insomnia-7/mc-launcher/releases/tag/desktop-v1.0.0-preview.20)。

[源码 3ff3bfc](https://github.com/Insomnia-7/mc-launcher/commit/3ff3bfcbc047e6c53578851d7c508115e8bafc5c)。Windows单文件31723662字节，SHA-256：1069417bc458f4118e3863b0048df2289171154094feb1af12c8147da69165e0。1,507项检查、架构护栏、裁剪程序集、单文件启动及生产更新验签通过。

- 实例与资源搜索框移除提示文字，保留输入、Enter搜索及无障碍名称。
- 恢复资源列表可见滚动条，将搜索进度移出列表，搜索等待时仍可滚动已有结果。
- 返回资源页复用现有结果和滚动位置，不重复检索；首次检索推迟至转场完成，手动搜索和筛选仍可刷新。
- 按用户审看演示后的确认，替换为卡片淡出300ms、淡入300ms；旧页先移除，新卡片略微错峰出现。快速切页、减少动画、缩放及最小化中断不会留下透明内容。
- 提供通过独立构建文件接入经许可项目API Key的能力，不把Key写进源码。实际提供的凭据被官方搜索端点以403（missing or invalid）拒绝，未内置该凭据；CurseForge搜索启用仍待正确的第三方API Key。
- 区分Upload API Token与第三方搜索API Key；无效Key给出明确提示，不再误导为只能按项目ID查询。申请说明见docs/curseforge-project-api.md。

验收与发布记录见docs/resource-search-acceptance.md。

## 1.0.0-preview.19 · Rust 客户端与导航职责拆分

2026-11-16。预览版；[Release](https://github.com/Insomnia-7/mc-launcher/releases/tag/desktop-v1.0.0-preview.19)。

[源码 49a0f14](https://github.com/Insomnia-7/mc-launcher/commit/49a0f14765f1835b3765e83c9fb0e7e0421f7478)。Windows单文件31718030字节，SHA-256：30bbfaa62c2f9add28659f9a22b7704e4264dfaef796e8fd4b76c1343ba14898。1,487项检查、架构护栏、裁剪程序集、单文件启动及生产更新验签通过。

- 主页和实例页增加 Rust 客户端入口，保留 Java 启动链及模组加载器。
- 支持 Horizon 官方 Nightly 的版本绑定 Ed25519 签名与安装包 SHA-256 验证，亦可导入完整本地构建；查询真实 Launch API 后建立独立 Rust 实例。
- 登录拥有 Java 版的微软账号后可准备资源并启动，首次转换自动准备对应 Java；会话只经标准输入传递，日志脱敏。取消准备终止工具进程，实例和存档保留。
- 网络页独立管理连接状态、全局下载源和启动器代理；设置仅保留外观、存储、同步及更新。消除外观从网络跳转的混用，并修复两个新路由的侧栏指示条。
- 保留 300ms 整页转场及共用输入/弹出控件样式。Horizon 属于上游实验性客户端，Java 模组不能直接运行。完整游戏实测待合法账号；其他设备项继续待测。

验收与发布记录见 docs/runtime-navigation-acceptance.md。

## 1.0.0-preview.18 · 输入与弹出控件风格统一

2026-11-16。预览版；[Release](https://github.com/Insomnia-7/mc-launcher/releases/tag/desktop-v1.0.0-preview.18)。

[源码 7287a2a](https://github.com/Insomnia-7/mc-launcher/commit/7287a2a40dc3ed65abc09b1b7355d1ef3d35ce0c)。Windows单文件28978193字节，SHA-256：00d0f530f0008ae0968d7ab0a82f830fd6a147d3953e9d11cfe98ce69c780080。1,410项检查、架构护栏、裁剪程序集、单文件启动及生产更新验签通过。

- 修复离线用户名、实例搜索及其他单行输入框的光标偏上；文字与占位提示垂直居中，多行/换行文本保留顶部对齐。
- 账号、导入、实例更多操作及剪贴板菜单统一圆角面板和紫色交互状态；包含独立弹出窗口与子菜单。
- 分组、版本、加载器、平台、排序、Java、主题与动画等下拉采用统一悬停、选中、焦点和禁用外观。
- 同步统一账号/实例列表、收藏/连续滚动等勾选框及内存/分辨率数字框；修复数字框内外重复焦点描边。
- 深浅主题和现有页面进行回归，检验范围见 docs/ui-controls-acceptance.md。发布构建及检查数量已记录，远端验证结果见验收文档。


## 1.0.0-preview.17 · 无叠印整页转场与导航修复

2026-11-16。预览版；[Release](https://github.com/Insomnia-7/mc-launcher/releases/tag/desktop-v1.0.0-preview.17)。

[源码 ce9ce6e](https://github.com/Insomnia-7/mc-launcher/commit/ce9ce6ec7d2d0c79cd93d1b91606add131c6496d)。Windows 单文件 28955665 字节，SHA-256：99427e693b0bc8188e19effc622f22fc141f7a7b2d445f5c7588e4261531cd5f。1311 项检查通过，另通过架构护栏、裁剪程序集回归、单文件启动与生产更新验签。

- 修复栏目切换时旧内容与新内容叠印；按用户确认采用约 300ms 的整页上下滑动，页面保持不透明，方向跟随导航顺序，转场结束清空旧页。
- 修复“收起侧栏 → 切换栏目 → 展开”的指示条横向错位，补上横向滑动与完整按钮尺寸变化处理。
- 安装方式改用 56 DIP 矢量重绘示意图：Fabric 布料、Quilt 拼布、Forge 铁砧、NeoForge 狐狸；保留原版和所有安装命令。
- 完成 Horizon 原生客户端的第三方接入与授权风险调研。本版未集成 Horizon 引擎，详细资料见 docs/horizon-integration-assessment.md。
- 当前设备完成 1,311 项相关检查及架构护栏，裁剪程序集、单文件启动和受信签名验证通过；远端结果及跨设备待测项见 docs/ui-navigation-acceptance.md。


## 1.0.0-preview.16 · 参考图首页与 MC 光影横条

2026-11-16。预览版；[Release](https://github.com/Insomnia-7/mc-launcher/releases/tag/desktop-v1.0.0-preview.16)。

[源码 3519bf6](https://github.com/Insomnia-7/mc-launcher/commit/3519bf6b847c04787cac35b82163e23c8fbf7838)。Windows 单文件 28947473 字节，SHA-256：f2ad00c52702f5c4e1ed4bf0e9b7c85cdbc0f87c601f138a578bba62c8963d9a。1257 项检查通过，另通过架构护栏、裁剪程序集回归、单文件启动与生产更新验签。

- 以用户提供的参考图重新实现首页：固定宽幅 MC 生存光影横条、四块双列卡片、三条实例预览及三列两行快捷功能，保留原启动器图标。
- 内置原创生成的黄昏风景位图；标题使用原生文字，空库和深浅主题中均保留横条。生成来源与提示词见 docs/assets/home-banner-prompt.md。
- 七项侧栏按参考图排列，网络入口直达现有设置；采用薰衣草浅色、炭灰蓝深色、紫色按钮与白色按钮文字。
- 所有启动、实例管理及快捷功能继续连接真实业务；未测速和未检测 NAT 如实显示，不使用参考图的演示状态。
- 当前设备 1,257 项检查、架构护栏、发布包及签名验证通过；远端结果见验收记录。Win10、真实高 DPI、真实远端联机继续待测。


## 1.0.0-preview.15 · A 方案启动器 UI 全面重构

2026-11-15。预览版；[Release](https://github.com/Insomnia-7/mc-launcher/releases/tag/desktop-v1.0.0-preview.15)。

[源码 aeb1992](https://github.com/Insomnia-7/mc-launcher/commit/aeb1992c7f0146ad8526b5356ab30ac88f8ab197)。Windows 单文件 25768714 字节，SHA-256：`f8eb2d6863e0d0790725e1eca7de76198164279d044635b378f98eb6db3f439e`。1223 项检查通过，另通过架构护栏、裁剪程序集回归、单文件启动和生产更新验签。

- 按用户选定的 A 方案重建应用外壳、导航与页面层级，保留现有启动器图标；专注 Minecraft 安装、实例管理与启动。
- 主页改为当前实例、账号与启动的连续区域；最近实例最多四条，运行环境按需展开；实例页分离选择、管理与启动。
- 实例详情采用局部导航与表单；联机、资源分类、任务、账号和设置重新布局，保留现有服务和数据格式。
- 重建紫色深浅双主题、按钮状态与原生窗口行为；补充 M0–M6 阶段检查、实际渲染与 Windows 原生验收。
- 本批仅重建 Windows x64 程序。其他系统与真实高 DPI/远端联机项目仍按验收记录待测。


## 1.0.0-preview.14 · M9 当前电脑验收

2026-11-15。预览版；[Release](https://github.com/Insomnia-7/mc-launcher/releases/tag/desktop-v1.0.0-preview.14)。

[源码 e3b2068](https://github.com/Insomnia-7/mc-launcher/commit/e3b206802aafef714ae4ea98f590c5ff892d9dde)。Windows 单文件 25768762 字节，SHA-256：`44abf8e858df8420c89ebcddde71d99842f04462c249da4cdeae82c76ca29f2d`。1844 项编号检查通过，另通过架构护栏、裁剪程序集回归与单文件启动。

- 新增 M9 专项检查，验证 240/72px 侧栏、首页横幅、三种窗口尺寸、连续导航与主题切换、减少动画和未知主题兼容。
- 当前 Windows 11 实测 8 个边角缩放命中、标题栏双击最大化/还原、实际 Mica 与实色回退；导出多倍率渲染图，明确区别真实系统 DPI。
- 使用正式安装与进程适配器验证真实 Minecraft 1.20.1 的安装取消/重试、实例选择、启动、图形/音频初始化与退出码 0。
- 修复旧 WPF 资源页只读文字采用双向绑定引发的渲染异常，并更新旧验收脚本以符合现行目录及更新行为。
- 两个解决方案构建成功，共 1844 项编号检查及架构护栏通过；当前环境验收结论与未测项目详见 [M9 报告](docs/ui-m9-acceptance.md)。其他设备 DPI、Win10、鼠标拖拽/Snap 飞出面板和真实联机仍待补测。

## 1.0.0-preview.13 · M8 开发预览页

2026-11-15。预览版；[Release](https://github.com/Insomnia-7/mc-launcher/releases/tag/desktop-v1.0.0-preview.13)。

[源码 72a8792](https://github.com/Insomnia-7/mc-launcher/commit/72a8792d0266ce43451d78909dc4a57f7598f250)。Windows 单文件 25768771 字节，SHA-256：`f2110d693c546ba3439888305cf8420e4d82627b41c89ac3ac78bde43711c0cb`。1554 项检查通过，另通过架构检查、裁剪程序集回归与单文件启动。

- 新增 M8 开发预览页，集中检视按钮八态、五种变体、双主题与跟随系统、Tooltip、弹窗、四态通知、卡片和七段交错入场。
- 仅在启动进程设置 `FANGYU_DEV_SHOWCASE=1` 时显示“UI 预览 (Dev)”与注册 `Ctrl+Shift+D`；普通模式不挂载页面，命令与快捷键不能进入。
- 主题、动效和 240/72px 侧栏直接联动现有外壳，预览导航使用共享选中胶囊和指示条。
- 演示使用独立通知队列、弹窗和可取消的模拟异步命令；成功/失败反馈仅表示演示结果，不调用游戏、安装、账号或联机服务。
- 七段交错动效支持重播、快速重播与中途减少动画；离页清理模拟操作、通知和弹窗，缓存页面保留滚动与输入。
- 新增普通模式/无效开关各 7 项门控检查、35 项预览界面检查和 12 项 Windows 原生检查。下一阶段为 M9 最终验收。

## 1.0.0-preview.12 · M7 全局组件

2026-11-15。预览版；[Release](https://github.com/Insomnia-7/mc-launcher/releases/tag/desktop-v1.0.0-preview.12)。

[源码 9047b74](https://github.com/Insomnia-7/mc-launcher/commit/9047b74989c358a22a6391b4d39f6dcf28db3721)。Windows 单文件 25757289 字节，SHA-256：`e8100e2d02db2af2d46f8643cd68783cf4eadd9bb43efa0abc52d2185f167f9f`。1493 项检查通过，另通过架构检查、裁剪程序集回归与单文件启动。

- 统一按钮八态：120ms 悬停、85ms 按下、140ms 松开，2px 键盘焦点环与瞬时 0.45 禁用态；异步命令执行中显示加载图标并阻止重复操作。
- 成功与失败图标接入联机业务确认结果，不把异步命令结束直接当作成功；失败保留重试入口。
- 四类模态弹窗统一使用 180ms 遮罩淡入、220ms 缩放与位移入场、150ms 退场；完成退场后才移除遮罩，支持快速反向、焦点恢复和减少动画。
- 原单条底栏提示升级为右下角最多四条通知，支持四种语义、单条关闭、动作与默认五秒自动淡出；任务结果和设置同步沿用原业务入口。
- Tooltip 统一 450ms 延迟与 140ms 淡入、4px 位移；关闭动画时立即呈现并清理过渡。
- 新增 29 项组件与 17 项 Windows 原生检查，覆盖真实异步防重入、业务反馈、弹窗中间帧、通知动作/队列/计时和 Tooltip。下一阶段为 M8 开发预览页。

## 1.0.0-preview.11 · M6 首页与真实业务卡片

2026-11-15。预览版；[Release](https://github.com/Insomnia-7/mc-launcher/releases/tag/desktop-v1.0.0-preview.11)。

[源码 6080a7f](https://github.com/Insomnia-7/mc-launcher/commit/6080a7f6d4af1f680ea9a24b63b86f701a829b1a)。Windows 单文件 25748314 字节，SHA-256：`0d316df97b5b47bb9ab49d9a005b79d97caf635dfdc61458330c74467f861ea7`。1447 项检查通过，另通过架构检查、裁剪程序集回归与单文件启动。

- 新增默认主页与独立导航，保留原完整游戏实例管理页。
- 88px 横幅紧贴内容顶部，144px 快速启动与网络区域，下方实例列表填满剩余高度，常用六个入口始终可达。
- 快速启动、实例选择、真实进度、行内启动和实例操作复用已有业务命令，主页不受管理页筛选条件影响。
- 联机状态来自真实组件和会话，延迟仅显示实际检测结果；更换目标、失败和断开时清除旧值，不主动联网填充状态。
- 七段首次入场完整接入首页，启动入口不等待动画；减少动画可立即归位。
- 新增首页布局、交互及 Windows 原生动效验收；修复覆盖式滚动条遮挡实例菜单点击的问题。M7–M9 继续推进。

## 1.0.0-preview.10 · M5 页面动效

2026-11-15。预览版；[Release](https://github.com/Insomnia-7/mc-launcher/releases/tag/desktop-v1.0.0-preview.10)。

[源码 dada037](https://github.com/Insomnia-7/mc-launcher/commit/dada03789fa86d5fb3ba85c0f24c8e3f7e7823d2)。Windows 单文件 25736174 字节，SHA-256：`56642a9aea480a90aba2b271588363eb87d388d2cc9f0104fc06c4197d670c7c`。1355 项检查通过，另通过架构检查、裁剪程序集回归与单文件启动。

- 统一页面、侧栏、文字和基础按钮的动效时长。
- 页面切换增加旧页 110ms 上移淡出与新页 200ms 淡入；实色背景托底，完成后清除旧页。
- 快速往返复用已有页面，保留筛选和滚动；中途关闭动画、卸载和最小化还原均可恢复。
- 游戏库首次可见模块使用 220ms / 40ms 交错入场，启动入口始终可用。完整首页的七段业务布局随 M6 接入。
- 通过页面、侧栏、标题栏、核心业务及桌面回归；本批为 M5 阶段预览，M6–M9 继续推进。

## 1.0.0-preview.9 · M4 侧边栏交互

2026-11-15。预览版；[Release](https://github.com/Insomnia-7/mc-launcher/releases/tag/desktop-v1.0.0-preview.9)。

[源码 e939670](https://github.com/Insomnia-7/mc-launcher/commit/e939670b67cc3890583eabfc5eeeb2214424eb7d)。Windows 单文件 25733124 字节，SHA-256：`5c17ca90a0d8566a232f94b46e856b2e2e52cb27c032855e3e8c73e278c3ce4a`。1327 项检查通过，另通过架构检查、裁剪程序集回归与单文件启动。

- 实现 240/72px 侧边栏分步文字动画，展开先扩宽再渐入，收起先渐隐再切换紧凑布局；连续点击从当前宽度反向衔接。
- 选中背景胶囊与唯一 3×20 指示条以 190ms 平滑跟随页面；保留窄窗口浮层，减少动画时立即落位。
- 收起态增加 450ms 中文 Tooltip、32×32 账号入口与更新入口；已加载头像可直接复用。
- 任务中心角标显示真实未完成数量，任务完成后自动更新；导航保留所有现有业务模块，主页入口随 M6 接入。
- 新增无头与 Windows 原生窗口侧栏专项验收；M5–M9 继续按计划推进。

## 1.0.0-preview.8 · 三态主题、材质适配与一体化标题栏

2026-11-15。预览版；[Release](https://github.com/Insomnia-7/mc-launcher/releases/tag/desktop-v1.0.0-preview.8)。

[源码 0f50b36](https://github.com/Insomnia-7/mc-launcher/commit/0f50b361bb3296492938274064a836b4558289c7)。Windows 单文件 25730875 字节，SHA-256：`9446025dc429860d20549e0da823ef7f2e27b8cf6664bf0f4ff9503737dd566a`。1282 项检查通过，另通过架构检查、裁剪程序集回归与单文件启动。

- 交付此前已合入的深色 / 浅色 / 跟随系统主题、设计资源和 240px 展开侧栏。
- M2：补齐 Windows Mica / Acrylic / 实色回退，仅标题栏和侧栏使用高不透明度材质色，业务内容保持实色；自定义壁纸优先。
- M3：标题栏收紧为 38px，保留原始品牌标识并显示当前真实页面或实例名称；新增三态主题快切、真实后台任务入口和键盘焦点提示。
- 保留原生窗口拖动、双击最大化、边缘缩放和 Snap Layouts 命中角色；最大化使用 OffScreenMargin 修正边界，还原时恢复细描边。
- 修正快速最大化再还原时，标题栏图标可能被上一段动画覆盖为旧状态的问题。
- 新增标题栏与材质专项检查和 Windows 原生窗口检查，并记录 M0–M9 实施进度。M4–M9 继续按计划推进。
- Linux/macOS 继续使用现有版本，本批不重新发布。四种系统 DPI 实机验收留待 M9。

## 1.0.0-preview.7 · 界面与动效体系重构

2026-11-14。[Release](https://github.com/Insomnia-7/mc-launcher/releases/tag/desktop-v1.0.0-preview.7)。

[源码 ad050c8](https://github.com/Insomnia-7/mc-launcher/commit/ad050c83297d4bf6be7003e06f94b415328d0ebe)。

Windows 单文件 25721435 字节，SHA-256：`85661d8ba20539c97aa329636554b5c800b82bd84a01a94685d309eeed59bfeb`。341 项桌面界面检查（含 4 项侧栏四态、3 项切页残留/瞬时切换）、880 项核心、47 项平台检查通过，另通过架构检查与 31 项联机界面检查。

按《方隅 Launcher · UI 动效设计大纲》重画整套外壳：先定 token，再改控件模板，最后才动布局。

- 新增动效 token 层：时长只有 83 / 167 / 250 / 420 四档，曲线只有进场 `0,0,0,1`、退场 `1,0,1,1`、颜色 `linear` 三条；项目里不再出现第五个时长或第四种曲线。
- 「流畅动画 / 跟随系统 / 关闭动画」三档开关成为唯一开关点：关闭时由整窗一个 `reduced-motion` 类清空全部过渡，保留瞬时状态反馈。
- 侧边栏从两态扩到四态：展开 236 / 收起 72 / 极窄 0（汉堡键开浮层）/ 浮层 236。收放沿用上一版的手感：只有容器宽度做 250ms 过渡，内边距、文字、品牌行与状态一次性切换，内容随宽度连续重排（实测 236→72 用 250ms，导航列全程跟着面板走）。
- 收起态沿用上一版：内边距 8、导航按钮内容居中、折叠键图标 167ms 翻转；选中项仍由一条共享蓝色竖条平移到位。
- 按钮状态机重做：悬停 83ms 换色、按下 83ms 收回 scale 0.98、松开 167ms 弹回；去掉回弹曲线与悬停位移。焦点环改为瞬时 2px `#4C8DFF`，不再有淡入。
- 控件圆角统一到 8 / 12，描边全项目 1px；主色 `#4C8DFF` 统一用于焦点环、选中竖条、进度条与勾选。
- 游戏库卡片补齐悬停反馈（抬 2px + 预渲染阴影层切透明度，不动画 BoxShadow），悬停不再铺灰底、按下仍带水波纹。
- 页面切换、详情页内分页、弹窗、卡片入场、列表切视图全部改为瞬时：不再有页面转场，也就不存在闪白、残留、叠页。
- 资源搜索的「最多等待 25 秒」提示不再出现。
- 资源列表行与游戏库卡片悬停不再铺灰底（鼠标扫过一片灰会像被选中），按下水波纹保留。
- 启动器被重复打开时安静退出（退出码 0），不再在单实例交接里抛 `Dispatcher shut down` 崩溃。
- 标题栏压缩到 40px，控制按钮补齐悬停 / 按下 / 关闭红 / 失焦降灰，最大化图标交叉淡入 167ms，窗口首帧 250ms opacity + scale 0.96 入场。

## 1.0.0-preview.6 · 删除可靠性与界面精简

2026-11-14。[Release](https://github.com/Insomnia-7/mc-launcher/releases/tag/desktop-v1.0.0-preview.6)。

[源码 72dc112](https://github.com/Insomnia-7/mc-launcher/commit/72dc112ff754704e89983fa02a861e2ea7029269)。Windows 单文件 25720303 字节，SHA-256：`b3e51e87890c7cf413c9ecac1f99b493eabc4c2804723777c1cd70f9bd6317f9`。880 项核心、333 项桌面、31 项联机界面检查通过，另通过架构检查与打包 EXE 的实际窗口启动检查。

- 删除版本成功后回到游戏库主页面并提示结果；失败保留当前页面并显示原因，界面收尾异常不再把成功改写成失败。
- 新增 hidden-versions / hidden-instances 墓碑：依赖保留的父版本与半删目录不会被重新扫成实例，删除共用同一版本的实例不会误隐藏其它实例；重装同版本自动清除墓碑并回收已删净的版本墓碑。
- 删除前拦截正在运行的版本与排队中的下载/安装任务；清理 library 偏好与专属封面；清理只读文件属性。
- 侧栏支持收起 / 展开（236 / 72px，状态持久化，导航项 Tooltip）。收起态品牌图标、折叠按钮、导航图标纵向分开不重叠，折叠按钮恢复 42×32 触区并改透明底。
- 导航改用共享蓝色指示条，随页面切换与侧栏宽度过渡平滑移动。
- 外观只保留日间白主题，移除标题栏日夜切换，历史夜间设置自动迁移。
- 首页移除「方隅-联机」横幅；首次启动直接进入主页面，不再自动弹出安装向导；向导内隐藏纵向滚动条；底栏移除「就绪」与右下角 JAVA EDITION。
- 按钮加入 Material 3 式水波纹与 state layer，悬停轻微放大、按下收回到 0.955，均带渐变过渡。
- 联机房间操作状态机（创建 / 连接 / 断开），失败文案按动作归属，会话卡显示实际地址、端口、节点与房间网络。
- 资源版本抽屉默认关闭并在进入页面时重置；检索增加 25 秒超时保护并给出可读提示。

## 1.0.0-preview.5 · 局域网与跨网络私密房间

2026-11-05。[Release](https://github.com/Insomnia-7/mc-launcher/releases/tag/desktop-v1.0.0-preview.5)。

[源码 3535467](https://github.com/Insomnia-7/mc-launcher/commit/3535467a0dfb23d867c8680bd8db12207a734095)。Windows 单文件 25719525 字节，SHA-256：`9011a4b769bed2e745a5015ca9b6b4e609a569f7941d137d25e81422c47cce0a`。1216 项回归与联机检查通过，另通过架构和实际 EXE 启动检查。

- 新增联机页面：按需发现局域网世界，选择实例，检测游戏连接、复制地址并启动连接。
- 本机世界可创建 EasyTier 私密房间，通过有时限的邀请连接；支持自行选择公共或自有节点、退出确认与断开清理。
- 联机组件从官方固定版本按需下载，校验大小与 SHA-256；不安装虚拟网卡、驱动或系统服务，不修改 DNS 或默认路由。
- 游戏地址作为一次性启动参数，不改写实例配置；提示房间与实例版本不匹配，保留黑白主题及原色图标。
- 真实本机三节点转发通过；公共旧默认域名无法解析，异地双机游玩仍需可用节点试验。本版不含公开大厅，Linux/macOS 发行包仍为 preview.1。详见 [联机使用与验证范围](docs/multiplayer.md)。

## 1.0.0-preview.4 · 实例图标与整合包标志

2026-11-04。[Release](https://github.com/Insomnia-7/mc-launcher/releases/tag/desktop-v1.0.0-preview.4)。包含下方 preview.3 的全部目录、任务与界面修复。

- 原版、Forge、Fabric、NeoForge、Quilt 使用各自原始图标；根据版本依赖和实例元数据识别加载器，自定义名称不再影响识别。
- 显示顺序为手动封面 → 整合包自带图标 → 平台项目图标 → 加载器标志 → 原版草方块。
- 导入读取包根目录、overrides 与 MultiMC/Prism 本地图标；在线安装缺少自带图标时保存 Modrinth/CurseForge 项目标志。重启后无需联网补图。
- 包图标完整等比显示，保留原色和透明通道；移除手动封面后恢复包图标。取消导入时清理暂存图标，原有实例不受影响。

[源码 b8b0b3e](https://github.com/Insomnia-7/mc-launcher/commit/b8b0b3e95822438fe76fad431393a61f2296f311)。Windows 单文件 25682420 字节，SHA-256：`0380d985ad64aaab867dcb08f51a55158e51e453db664fdf6a2ceae2b8021d12`。相关 96 项检查通过，实际 EXE 启动验证通过。见 [图标说明](docs/instance-artwork.md)。

## 1.0.0-preview.3 · 目录、任务与界面修复

2026-11-04。[Release](https://github.com/Insomnia-7/mc-launcher/releases/tag/desktop-v1.0.0-preview.3)。补发先前仅本地交付的原始程序。

- 安装前由玩家确认游戏位置，不再自动创建替代游戏库；设置集中提供位置选择、游戏库及启动器数据的复制迁移。
- 查找已有游戏库先展示候选，外部库经核对运行目录后复制导入，保留来源与存档。
- 存储扫描遇到链接或读取异常时保留其他分类结果；展示占用、可回收量和跳过项，支持取消与已知缓存清理。
- 下载任务区分进行中和历史记录；支持单条移除与清空已完成记录，清除记录不会删除游戏。
- 统一中文字号与控件排版，优化搜索占位、翻页过渡、错误恢复与迟到请求处理；调整默认插画。

[源码 ace029e](https://github.com/Insomnia-7/mc-launcher/commit/ace029ebd9f2e498b345c37228ad765867f08d10)。Windows 单文件 25633179 字节，SHA-256：`205ca331629942e3bd1fca04304fd81ccf3f62659c92d17856738ff852e38368`。既有回归与专项共 1208 项检查通过，另通过架构和原生启动检查。见 [处理及验收说明](docs/preview3-remediation.md)。

以上两版更新 Windows x64。Linux/macOS 原生发行包仍为 1.0.0-preview.1，最新发行页保留其原始签名更新清单和下载链接；未将旧平台程序改标为新版本。后续每批改动按 [发布约定](docs/release-policy.md) 同步源码与 Release。

## 1.0.0-preview.1 · 跨平台预览版

2026-10-31。[Release](https://github.com/Insomnia-7/mc-launcher/releases/tag/desktop-v1.0.0-preview.1)。

- 新增 Linux x64 / ARM64 便携包、macOS Intel / Apple Silicon 应用包，保留方隅名称与原色图标；Windows 继续交付单 EXE。
- 接入 Linux Secret Service 与 macOS 钥匙串，账号文件使用 AES-GCM 密文；系统凭据不可用时不回退明文。
- Java 自动下载按系统和架构选包，支持 tar.gz、macOS Contents/Home、Unix 执行权限，实际探测成功后才提交；修正 ARM64 识别。
- 签名更新按平台分发，拒绝错误系统或架构；macOS 更新全部应用资源与签名，Unix 备份保存在数据目录，支持重启确认与失败恢复。
- 新增四平台原生系统检查，覆盖真实密钥环、官方 Java 下载与运行、打包后窗口启动、更新进程成功、失败回退与篡改拒绝。

使用与验收范围见 [跨平台预览说明](docs/platform-preview.md)。macOS 尚未 Developer ID 签名或公证；真实微软授权、Minecraft 图形运行和全部模组组合不在自动验证范围内。

[源码 5e01b4c](https://github.com/Insomnia-7/mc-launcher/commit/5e01b4c52efbbcac479d08e3bc4c5cef112925a4)。Windows 单文件 23911658 字节，SHA-256：`dfc21c15811246a6ee4bfba5ff8914ba136a81ebfc13b8b83ddab80e8bfdad15`。1470 项检查通过：Windows 回归与平台检查 1226 项，四种 Unix 平台各 61 项；另验证 2 次 Windows 成品启动、6 组 0.9 → 1.0 原生更新。[四平台原生系统记录](https://github.com/Insomnia-7/mc-launcher/actions/runs/41186982952)。

## 0.9.0-preview.1 · 设置同步与动效版

2026-10-31。[Release](https://github.com/Insomnia-7/mc-launcher/releases/tag/desktop-v0.9.0-preview.1)。

- 新增本机游戏设置持续同步，可选择按键、音量、画面、语言与服务器列表；实例默认不参与，画面与服务器默认不勾选。
- 同步前展示差异和冲突，首次不同的设置默认保留本地；支持实例独立选项、启动前同步、来源退出后刷新及备份恢复。
- 保留未知游戏选项、注释与换行；不复制 Java、内存、账号、模组配置或存档。服务器列表按整份文件处理。
- 统一页面、详情标签、按钮与主题过渡；展开内容按高度揭示，不缩放文字，快速反向操作衔接当前进度。
- 新增可关闭的任务与设置结果提示，支持减少或关闭动画；实例设置的保存操作固定在底部。

使用步骤与范围见 [游戏设置同步与统一动效](docs/settings-sync-and-motion.md)。没有新增运行库或网页内核。

[源码 e62bd34](https://github.com/Insomnia-7/mc-launcher/commit/e62bd34d0ab9e5539ab2495c59772f4d9831d3d6)。Windows 单文件 23863706 字节，SHA-256：`04bcc69845d1aa461c9a0551f25666025505da1978d72a294c9444d886a80afa`。1177 项回归检查通过；另验证 2 次独立成品启动、6 组 0.8 → 0.9 原生更新情形。

## 0.8.0-preview.1 · 整合包升级版

2026-10-31。[Release](https://github.com/Insomnia-7/mc-launcher/releases/tag/desktop-v0.8.0-preview.1)。

- 整合包详情新增原色封面、介绍与独立版本安装入口，支持日夜主题；保持原生界面，无新增浏览器运行环境。
- 整合包升级提供文件差异、基线/本地/上游比较、JSON 对象字段合并、逐项冲突选择，以及更新前文件备份和恢复。
- 新平台安装记录项目来源，可选择正式版、测试版、开发版更新；本地更新包与无基线旧实例采用保留本地内容的默认策略。
- 启动前和实例体检增加常见模组必需前置、版本范围、重复 ID 与声明冲突检查；未知规则显示提示。
- 下载任务完成后可自动接续，等待任务可调整顺序；暂停、失败、取消和关闭时停止接续，保存用户偏好。

使用步骤与范围见 [整合包升级说明](docs/modpack-upgrades.md)。Linux / macOS 尚未完成发行实机验收。

[源码 d5b8146](https://github.com/Insomnia-7/mc-launcher/commit/d5b81469cdefcc7e3f8016c309dde369785518bf)。Windows 单文件 23832245 字节，SHA-256：`d40f2b7ac821f2219de90b98764542a8bfcfdc9841ad6a50a28056e23b8f2c42`。1130 项回归检查通过；另验证 5 项真实 Modrinth 数据、2 次独立成品启动、6 组 0.7 → 0.8 原生更新情形。

## 0.7.0-preview.1 · 启动器自动更新版

首次接通桌面单文件自动更新：首屏后后台检测、设置中的更新入口与开关、版本 / 大小 / 更新说明、确认下载与取消、签名及 SHA-256 校验、自身临时副本更新、保留当前文件名、旧版备份、新窗口就绪确认及失败回退。补录旧 Release 不触发降级，同版本构建号不触发升级。旧版用户需先手动安装一次。详细行为见 [自动更新说明](docs/launcher-auto-update.md)。

[Release](https://github.com/Insomnia-7/mc-launcher/releases/tag/desktop-v0.7.0-preview.1) · [源码 36371a1](https://github.com/Insomnia-7/mc-launcher/commit/36371a169491ff3ca0322d77ba4d8da5ab94bb65)。Windows 原始单文件 23,783,841 字节，SHA-256：`10ca469356ca688217a87ddc79a7932d76f651f0181e199a14793cb97008f54d`。

通过 1085 项回归检查，另实测 6 组原生更新情形：中文、英文及自定义名称成功替换重启；无效新程序、文件占用与篡改包保留或恢复旧版。

---

记录包含已交付的 0.2.0-preview.1 至 1.0.0-preview.4。以下历史 Release 补录使用当时保留的原始 Windows EXE，并核对原验证记录或使用说明中的哈希；没有使用新版程序替代历史版本。

当前 Windows 推荐下载：[1.0.0-preview.4](https://github.com/Insomnia-7/mc-launcher/releases/tag/desktop-v1.0.0-preview.4)。Linux/macOS 见 [1.0.0-preview.1](https://github.com/Insomnia-7/mc-launcher/releases/tag/desktop-v1.0.0-preview.1)。机器可读记录见 [history.json](docs/releases/history.json)。

历史包早于源码提交打包，因此部分旧 EXE 的 ProductVersion 后缀仍指向前一次提交。下列源码提交对应功能版本；原始二进制版本后缀、大小与 SHA-256 均另行保留，不能将两者视为完全一致的构建来源标记。0.4、0.5、0.6 的成品版本后缀与源码提交一致。

历史补录不补写未做过的测试；以各版附件 README 和验证记录为准。Linux / macOS 未实机验收。

## 0.6.0-preview.1 · 存档维护版

功能源码日期：2026-10-31。[Release](https://github.com/Insomnia-7/mc-launcher/releases/tag/desktop-v0.6.0-preview.1) · [源码 203a6ee](https://github.com/Insomnia-7/mc-launcher/commit/203a6ee8941d639ec11f6c5f3c7e6cd97dcefc59)。

单世界列表、独立备份与恢复前自动备份；游戏依赖定向校验与补下载；手动存储扫描及旧完整缓存清理。

Windows 原始单文件：23770757 字节。

SHA-256：`f757b3d140ce7c70eb2695eea9c34d91a169b33c1f76fd3e5728e37247633794`。

## 0.5.0-preview.1 · 模组工作台版

功能源码日期：2026-10-31。[Release](https://github.com/Insomnia-7/mc-launcher/releases/tag/desktop-v0.5.0-preview.1) · [源码 5ae0af6](https://github.com/Insomnia-7/mc-launcher/commit/5ae0af69a2f288173d6932c6cc56f24c8bec7cdd)。

批量启停删与精确重装；更新前预览版本、频道、日志和前置；文件变化使计划失效；可选内容复制与独立试装副本；持久任务与操作前快照。

Windows 原始单文件：23745057 字节。

SHA-256：`136d31dd60d35172c9ef14517405476e957d237684c3031634e364bd33fc5811`。

## 0.4.0-preview.1 · 游戏工作区版

功能源码日期：2026-10-30。[Release](https://github.com/Insomnia-7/mc-launcher/releases/tag/desktop-v0.4.0-preview.1) · [源码 c12d9ed](https://github.com/Insomnia-7/mc-launcher/commit/c12d9ed1f5b2876b344b9a36a82c6ae0a39d5fdd)。

五个独立主导航页面；游戏库卡片与紧凑列表、搜索、收藏、分组、名称封面及最近游玩；实例详情分页与按需创建页面。

Windows 原始单文件：23702104 字节。

SHA-256：`2273fa907c2cd51f2f73cd3885d4474afb33975f27d3554139efc9db0c19e532`。

## 0.3.0-preview.3 · GPU 角色预览版

功能源码日期：2026-10-30。[Release](https://github.com/Insomnia-7/mc-launcher/releases/tag/desktop-v0.3.0-preview.3) · [源码 6ebd88a](https://github.com/Insomnia-7/mc-launcher/commit/6ebd88a8405209314d1240ed61ceee61a27293df)。

GPU 3D 皮肤和完整披风预览；分段连续摆动、自然动作、镜头缩放与复位；离开页面或失焦暂停动画，不支持时兼容回退。

Windows 原始单文件：23685378 字节。

SHA-256：`0765df00c749c6f52a224d4738c18f2bffa5eef1d80f75c7c0d10f1568e76115`。

## 0.3.0-preview.2 · 皮肤清晰预览版

功能源码日期：2026-10-30。[Release](https://github.com/Insomnia-7/mc-launcher/releases/tag/desktop-v0.3.0-preview.2) · [源码 7aa4cb2](https://github.com/Insomnia-7/mc-launcher/commit/7aa4cb2f0d21575a31b17bed108ffdf818d60e22)。

关闭皮肤像素平滑插值；增加面部明暗、透明通道保护、背面剔除和略俯视相机；修复 PNG DPI 对纹理坐标的影响。

Windows 原始单文件：23652893 字节。

SHA-256：`8b7a05d3941a392e5a891fc32fa4d56c705e77d6234f7f56fc1c8a24d642d66a`。

## 0.3.0-preview.1 · 玩家中心版

功能源码日期：2026-10-30。[Release](https://github.com/Insomnia-7/mc-launcher/releases/tag/desktop-v0.3.0-preview.1) · [源码 2ce75c8](https://github.com/Insomnia-7/mc-launcher/commit/2ce75c8cb68efd43e27a11e3b4d3c0900ab06a2e)。

正版皮肤上传与已有披风切换；离线衣柜及适配皮肤组件；实例快照和设置预设；更新频道与文件锁定；持久任务、实例体检与脱敏诊断导出。

Windows 原始单文件：23647909 字节。

SHA-256：`3494a1b2642f78326e808ba645a01aa7ae7c870b2a670dc11d11f0c7e9fce29f`。

## 0.2.0-preview.8 · 正式名称与透明图标版

功能源码日期：2026-10-25。[Release](https://github.com/Insomnia-7/mc-launcher/releases/tag/desktop-v0.2.0-preview.8) · [源码 68ddade](https://github.com/Insomnia-7/mc-launcher/commit/68ddadea06ac76270de18ca6c3d8f5711a68e8de)。

名称统一为方隅；应用、窗口、任务栏、标题栏与侧栏接入透明背景草方块图标；Windows EXE 使用方隅名称。

Windows 原始单文件：23557642 字节。

SHA-256：`0a053cba9631ae2c0545f2814b9a513ad3347daed8082d38b67ec014e82172e4`。

## 0.2.0-preview.7 · 连续浏览与导入清理版

功能源码日期：2026-10-25。[Release](https://github.com/Insomnia-7/mc-launcher/releases/tag/desktop-v0.2.0-preview.7) · [源码 6273a81](https://github.com/Insomnia-7/mc-launcher/commit/6273a81a9741dc56ce96db3fcf87d7812dc1d048)。

连续滚动与分页切换，每批 10 / 20 / 35 / 50 项；游戏版本删除确认；取消导入清理未完成文件；增大图标字体并移除选中黑条。

Windows 原始单文件：23002988 字节。

SHA-256：`73cb153e712af305f29df3163164bd06ca80fa028f491ce478b81d3653274b3a`。

## 0.2.0-preview.6 · 统一设置与标题栏版

功能源码日期：2026-10-25。[Release](https://github.com/Insomnia-7/mc-launcher/releases/tag/desktop-v0.2.0-preview.6) · [源码 7e048ab](https://github.com/Insomnia-7/mc-launcher/commit/7e048ab5d850a05aa24ed759150bf3e5fb759063)。

整合外观与网络设置；统一 Windows 标题栏；增大资源图标与介绍字体；资源包、数据包、光影不显示不适用的加载器说明。

Windows 原始单文件：22996902 字节。

SHA-256：`07dfa7efc0b5a7794d1c5679d1b4acfa0a9e14d65768f40fd14d035f48ba72d1`。

## 0.2.0-preview.5 · 下载中心布局优化版

功能源码日期：2026-10-25。[Release](https://github.com/Insomnia-7/mc-launcher/releases/tag/desktop-v0.2.0-preview.5) · [源码 a2e782f](https://github.com/Insomnia-7/mc-launcher/commit/a2e782f15d81e78fde155b862f1caec252b9e4f2)。

压缩搜索区与留白，扩大列表可视范围；移除英文文件名和中文简介来源提示行；区分正式、测试、开发版本下载。

Windows 原始单文件：22994231 字节。

SHA-256：`bf1c3871866813d1a164105c5371e472cdc5ca0dc3df030112fdc0f9298e4490`。

## 0.2.0-preview.4 · 下载中心增强版

功能源码日期：2026-10-25。[Release](https://github.com/Insomnia-7/mc-launcher/releases/tag/desktop-v0.2.0-preview.4) · [源码 7fb2e29](https://github.com/Insomnia-7/mc-launcher/commit/7fb2e290a321cdff1c0a4e4b4c7dc76237562401)。

项目原色图标、加载器、下载量与更新时间；选择资源发布版本；前置依赖提示与解析；名称中英对照和中文简介。

Windows 原始单文件：22992346 字节。

SHA-256：`4325fb39c8c9be91e05fefca8accc04fe9db7ea2b2d6d9eaa646e92859383479`。

## 0.2.0-preview.3 · 加载器选择版

功能源码日期：2026-10-25。[Release](https://github.com/Insomnia-7/mc-launcher/releases/tag/desktop-v0.2.0-preview.3) · [源码 3a162ff](https://github.com/Insomnia-7/mc-launcher/commit/3a162ff2e43dba702968aeef0f23c34e4733d094)。

选择 Minecraft 后配置原版或 Fabric / Quilt / Forge / NeoForge，选择兼容加载器版本；提供查询失败重试。

Windows 原始单文件：22963963 字节。

SHA-256：`0c073672fecc2a87f92ba9b11f43822c37b106fc5fc93cc0911717bd3a254f22`。

## 0.2.0-preview.2 · 动效优化版

功能源码日期：2026-10-25。[Release](https://github.com/Insomnia-7/mc-launcher/releases/tag/desktop-v0.2.0-preview.2) · [源码 00a9ccf](https://github.com/Insomnia-7/mc-launcher/commit/00a9ccffe8d9a6097cab310fcbfa551bd42ec4d5)。

隐藏首页滚动条，保留滚轮与键盘翻页；优化按钮阴影、悬停和按压过渡。

Windows 原始单文件：22767180 字节。

SHA-256：`f806486a12ca4db8fa51e3aa71085648fcd2e94cb0e214a1fff463e88f51325c`。

## 0.2.0-preview.1 · 黑白主题精简版

功能源码日期：2026-10-24。[Release](https://github.com/Insomnia-7/mc-launcher/releases/tag/desktop-v0.2.0-preview.1) · [源码 481810d](https://github.com/Insomnia-7/mc-launcher/commit/481810dbd10ddc917e5da4ee26f41e0304dd534d)。

黑白日夜主题、原色图标、按钮交互；精简自带运行环境的单文件发布。

Windows 原始单文件：22763140 字节。

SHA-256：`d28be0133d532d10efc09ae8751c915175c899ae6554d9bb7aff13772312460e`。

