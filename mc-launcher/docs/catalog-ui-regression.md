# 资源目录与搜索回归验收

2026-11-16。本批资源修改纳入 1.0.0-preview.21。用户已确认 B 启动方案及左侧项目依次舒展，正式接入情况见 startup-acceptance.md。原有 B 页面转场保留。

## 本批修改

- 资源分类保留平台原始 ID，显示中文名称。Modrinth 按类型、功能、分辨率、性能区分；数据包沿用平台的模组分类，但检索仍限定 datapack。CurseForge 的数字分类 ID 保持不变，常用分类使用中文名称。
- 游戏本体增加空白版本搜索框，即时按版本号、分组名、愚人节简介筛选，忽略版本号大小写，匹配分组自动展开。清空搜索恢复推荐与默认折叠。
- “活动版”改为“愚人节版本”，对显式识别的官方版本逐项提供独立撰写的简介，不按日期猜测分类，也不凭空增加清单中没有的可安装版本。
- 共享分组模板的标题按钮横向铺满，覆盖游戏版本、资源发布版本以及其他使用同一模板的抽屉。

## 已复现的问题与处理

1. **进度动画停止**：原生 Windows 中，搜索等待期间离开资源页，再返回缓存页，Fluent 进度条的第一段平移位置连续八次均为 0。首次进入页时的检查正常，无法捕捉该回归。新增 ActivityProgressBar 在重新挂载后的 Loaded 阶段重新激活不确定进度样式，恢复时钟；失效的挂载回调不修改控件状态。其他页面的同类等待进度条共用此控件，确定百分比的下载进度条保持原逻辑。
2. **结果被异步元数据清空**：分类列表更新过程中，下拉框会暂时写回空选择。此前它被当作用户改了筛选，清空结果并可能再次触发搜索。现在元数据替换期间不触发筛选变更，保留选中的原始分类 ID。真实用户改分类仍会清空旧筛选结果。

资源滚动列表仍放在有界的星号行中，进度区位于独立行，不挡住结果或滚动条。持续加载依然按平台原始行数推进偏移，避免重复。

## 验证入口

在 mc-launcher 目录运行相关检查，输出目录使用新的 D 盘路径：

```powershell
dotnet run --project tests/Core.Checks -c Release
dotnet run --project tests/Desktop.Checks -c Release -- D:/Codex/qa/catalog-new --catalog-refinement
dotnet run --project tests/Desktop.Checks -c Release -- D:/Codex/qa/interaction-new --resource-interaction
dotnet run --project tests/Desktop.Checks -c Release -- D:/Codex/qa/native-new --resource-native
dotnet run --project tests/Desktop.Checks -c Release -- D:/Codex/qa/live-new --live-scroll
```

- catalog-refinement：输入搜索、空结果、清空恢复、简介、分组宽度、晚到的元数据、滚动、取消、窗口宽度。
- resource-native：真实 Windows 滚轮、进度不遮挡、搜索中页面往返、浅深主题、缩放后的循环动画。采样跨越完整周期，避免把正常的停顿帧误判为卡住。
- live-scroll：Modrinth 五种资源真实检索、分类显示、鼠标滚动、追加去重、页面往返。
- 固定回归入口同时纳入本地 M4/M6 验收脚本和桌面发布工作流，后续发布必须通过分类、滚动与原生进度检查；联网检查仍为单独验收，避免外部平台临时不可用影响离线回归。本批未触发 GitHub 工作流。
- Core.Checks 使用本次官方分类清单快照，覆盖 77 个类别/功能/画质项的中文显示，原始 ID 与请求参数保持可追踪。后续平台新增未知分类时保留原名，避免错误翻译或阻止检索；更新清单时同步补充显示名称。
- 本次没有可用的 CurseForge 项目 Key，其分类请求结构和数字 ID 通过离线接口检查，未声称通过 CurseForge 在线搜索验收。

## 内容依据

- [Modrinth 官方分类清单](https://api.modrinth.com/v2/tag/category)
- [Mojang：Herdcraft，2026](https://www.minecraft.net/en-us/article/the-herdcraft-update)
- [Mojang：Craftmine，2025](https://www.minecraft.net/en-us/article/the-craftmine-update)
- [Mojang：毒马铃薯，2024](https://www.minecraft.net/en-us/article/poisonous-potato-update)
- [Mojang：一次一个方块，2022](https://www.minecraft.net/en-us/article/mojang-studios-release-new-astonishing-update)
- [Mojang：Minecraft 3D，2019](https://www.minecraft.net/en-us/article/minecraft-3d-rediscovered)
- [Avalonia 12.1.4 Fluent 进度条模板](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Themes.Fluent/Controls/ProgressBar.xaml)

仅作为功能和事实依据，未参考仓库的 UIdesign。
