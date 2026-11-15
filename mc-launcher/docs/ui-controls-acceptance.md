# 输入与弹出控件统一验收 · preview.18

状态：针对性验收通过，整机页面回归、实际发布包与远端发布验证进行中。

本批针对用户反馈的离线用户名 / 搜索光标偏上、账号菜单默认灰色方框、分组下拉黑色悬停框，审查共用控件及各页面实例。保持现有启动、登录、安装和 300ms 整页转场。

| 阶段 | 检验步骤与范围 | 判定 |
| --- | --- | --- |
| M0 定位 | 对照 Avalonia 12.1.4 官方模板，查找输入内边距、状态样式与独立弹出窗口的资源来源 | 对称内边距 + 单行居中；统一控件和模板内实际绘制层 |
| M1 输入 | 离线用户名、实例和资源搜索、版本过滤、分组/改名、地址/端口、代理、皮肤命名、JVM 参数、数字输入 | 空白/英文/中文光标几何；提示文字同轴；只读/禁用仍可识别 |
| M2 弹出层 | 顶栏账号、实例导入、主页实例操作、详情更多操作、输入剪贴板菜单、分组/版本/加载器/平台/排序/Java/主题/动画下拉 | 12 DIP 面板；8 DIP 项；紫色悬停、按下、选中和键盘焦点；子菜单跟随主题 |
| M3 选择控件 | 收藏、连续滚动、安装后启动、试装副本选项、皮肤预览选项、任务接续、账号及实例列表 | 圆角和紫色选中态；键盘操作；半选短横；禁用不执行；焦点不改变行内尺寸 |
| M4 页面回归 | 首页、实例/详情/模组/备份/设置/日志、资源目录/版本安装、联机、网络、衣柜、下载任务、外观与维护设置 | 现有命令、数据绑定和布局回归；深浅主题及紧凑窗口 |
| M5 当前 Windows | 实际原生窗口里的光标、菜单、子菜单、下拉、数字输入和勾选框；另保留原生标题栏/导航/转场验收 | 光标中心误差不超过 1.1 DIP；真实 popup root 截图；已测设备为 Windows 11、100% 缩放 |
| M6 发布 | 实际裁剪程序集、单文件启动、受信更新签名、GitHub 资产大小/哈希、生产更新客户端下载 | 构建源码与标签一致；预览版不设 Latest |

## 同类问题修复

- `Inputs.axaml` 作为应用级共用样式加载，覆盖全项目的 TextBox、ComboBox、MenuFlyoutPresenter、ContextMenu、FlyoutPresenter、MenuItem、ComboBoxItem、ListBoxItem、CheckBox、NumericUpDown。
- 单行框使用对称 `12,6` 内边距，并通过真实 TextPresenter 的光标矩形测量居中。多行、换行显示和浮动提示保留顶部对齐，避免邀请信息与长文本垂直居中。
- 原生文本编辑、选择、快捷键与剪贴板菜单保留。输入光标、占位文字、选择文字、焦点和错误描边使用现有配色。
- 独立弹出根节点中的菜单和下拉使用实时主题画刷；子菜单也使用相同面板资源，避免只修主窗口而遗漏弹出层。
- 列表项保留透明描边空间，键盘焦点不会使内容位移。勾选框使用独立勾号和半选短横。
- 数字框仍使用原生 NumericUpDown、ButtonSpinner 与 TextBox；外层统一描边，内部不再出现第二层焦点框；步长、上下限和只读行为保留。

## 验证方法与边界

`--controls` 使用隔离账号、实例和网络数据，验证深浅主题的实际控件几何、渲染、鼠标状态、点击、路由键盘动作和绑定错误。`--controls-native` 在 Win32 原生窗口验证实际排版与独立弹出根节点、路由键盘动作和业务命令；鼠标状态由前者检查，不冒称物理鼠标人工验收。

其他页面继续使用现有 shell/home/manage/catalog/multiplayer/account/consistency/native/navigation/page-motion 检查。截图、记录与测试配置只写入 D 盘隔离目录；不使用用户账号或游戏资料。

Windows 10、其他真实缩放比例、输入法组合过程与人工窗口手势继续列为待测；本批不改动系统文件选择器等原生系统对话框。真实游戏与远端联机不是控件样式检查的替代项。

模板依据：[TextBox](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Themes.Fluent/Controls/TextBox.xaml)、[ComboBox](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Themes.Fluent/Controls/ComboBox.xaml)、[MenuFlyoutPresenter](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Themes.Fluent/Controls/MenuFlyoutPresenter.xaml)。
