# UI 预览（M8）

适用于 `1.0.0-preview.13` 及之后的桌面版本，用于组件与动效走查。默认启动不显示开发入口。

在 PowerShell 为本次启动设置环境变量后运行程序：

```powershell
$env:FANGYU_DEV_SHOWCASE = '1'
& 'D:\Codex\2026-11-15\mc-launcher-main\mc-launcher\dist\preview.13\方隅.exe'
```

源码运行时，在仓库的 `mc-launcher` 目录执行：

```powershell
$env:FANGYU_DEV_SHOWCASE = '1'
dotnet run --project src/Launcher.Desktop
```

启动后点击侧栏底部的“UI 预览 (Dev)”，或按 `Ctrl+Shift+D`。该环境变量必须精确为 `1`；设为 `true` 或不设置均不启用。移除环境变量并重新启动后隐藏入口和快捷键：

```powershell
Remove-Item Env:FANGYU_DEV_SHOWCASE -ErrorAction SilentlyContinue
```

## 走查内容

- 外壳：浅色、深色、跟随系统；侧栏 240/72px 和共享指示条；动效选择。
- 按钮：八态固定样本、五种变体与 Tooltip；可用鼠标悬停、按住及 Tab 检查真实交互。
- 异步演示：运行预览操作，检查加载与防重入；选中“模拟失败”观察错误反馈和重试。
- 弹窗与通知：打开预览弹窗、输入、取消/确认；触发四态通知，查看单条关闭、动作、最大四条与默认五秒移除。
- 卡片与动效：悬停卡片上移、七段 220ms/40ms 交错入场；连续重播、减少动画和快速离页后应归位。
- 小窗口：760×620 下纵向滚动访问全部模块，无横向溢出；返回页面保留滚动与输入。

主题、侧栏和动效控制使用真实外观偏好，会按现有方式保存。模拟操作、预览弹窗与通知使用独立状态，不调用游戏、下载、账号或联机服务。离页会取消模拟操作并清理通知和弹窗；普通业务页面仍使用自己的通知队列。

## 自动化入口

`tests/Desktop.Checks` 提供 `--showcase-disabled`、`--showcase-invalid`、`--showcase` 和 `--showcase-native`。为每次运行提供新的空测试目录；测试数据、截图和临时目录均放在 D 盘。`--showcase-native` 需要 Windows 图形会话，验证实际动画中间帧和生命周期，不代表完成其他系统 DPI 或真实游戏验收。
