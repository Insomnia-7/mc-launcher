# UI 重构实施进度

更新日期：2026-11-15。本批版本：`1.0.0-preview.8`。

以 `01-milestone-plan-M0-to-M9.md` 为实施计划，以当前源码与实际检查结果判断进度；`samples/` 仍是参考代码，不是运行入口。

| 阶段 | 当前进度 |
|---|---|
| M0 | 已有工程审计；本批核对远端 main 与本地源码一致后，从独立开发分支开始 |
| M1 | 已有双主题色板、Tokens.axaml 与主题样式接入；全局组件中的历史颜色与动效资源继续在后续阶段统一 |
| M2 | 已有深色、浅色、跟随系统与持久化；本批补齐 Windows Mica / Acrylic / 实色回退及自定义壁纸互斥 |
| M3 | 本批完成 38px 标题栏、真实页面/实例名称、三态主题快切、后台任务入口、内描边与窗口状态同步 |
| M4 | 待实施文字分步动画；保留已有 240 / 72px 与窄窗口浮层行为 |
| M5 | 待实施统一页面切换与交错入场 |
| M6 | 待实施首页布局与真实业务卡片 |
| M7 | 待实施全局组件升级 |
| M8 | 待实施开发预览页 |
| M9 | 本批执行相关回归；最终全量验收在其余阶段落地后进行 |

## 本批实现

- 沿用 `MainWindow` 的原生装饰角色，不为拆分组件更改既有控件名称。
- 标题栏使用原始品牌图标；页面标题覆盖游戏实例、资源、角色、下载任务、联机、设置和实例详情。
- 主题按钮调用已有 `CycleThemeModeCommand`，模式、图标与无障碍说明同步更新，不重建窗口。
- 标题栏后台任务入口绑定真实持久任务状态，空闲时隐藏，点击进入原任务中心。
- Windows 按 Mica → AcrylicBlur → None 请求材质，仅在实际生效后使用透明窗口及高不透明度外壳底色；主要业务内容保持实色。
- 自定义壁纸优先，加载后停用系统材质，移除壁纸后恢复材质请求；非 Windows 继续使用原生边框和实色底色。
- 非最大化窗口显示不拦截点击的细边框；最大化沿用 `OffScreenMargin`，快速切换窗口状态后图标与真实状态保持一致。

## 验证范围

执行 `MinecraftLauncher.Desktop.slnx` Release 构建、架构检查、Core.Checks、Desktop.Checks 主套件，以及以下专项入口：

```powershell
dotnet mc-launcher/tests/Desktop.Checks/bin/Release/net10.0/Desktop.Checks.dll <新的空目录> --shell
dotnet mc-launcher/tests/Desktop.Checks/bin/Release/net10.0/Desktop.Checks.dll <另一个空目录> --shell-native
```

`--shell` 验证页面标题、主题循环与持久化、页面状态保留、材质回退、壁纸互斥、760/820/1180px 布局及快速最大化还原后的图标。

`--shell-native` 在真实 Windows 窗口上验证拖动区 HTCAPTION、主题按钮 HTCLIENT、最大化按钮 HTMAXBUTTON（Snap Layouts 命中角色）、窗口外框 HTLEFT、双击最大化、还原、最小化与最大化边界，并记录实际材质和当前屏幕缩放。

本机实际生效材质为 Mica，屏幕缩放为 100%。100% / 125% / 150% / 200% 图片输出已经生成；这些是渲染倍率检查，不等于四种系统 DPI 实机测试。Windows 10 实机材质回退、其他系统 DPI 的窗口行为与 Snap 浮层的人工操作留待 M9 验收。Linux/macOS 本批不重新发布。

## 发布

用户已选择继续发布预览版。新版本使用新的 `desktop-v1.0.0-preview.8` 标签与 Pre-release，上传 Windows 单文件、受信密钥签名清单、校验文件及说明，不占用正式版 Latest 入口。其他平台沿用已有版本及原始下载链接。
