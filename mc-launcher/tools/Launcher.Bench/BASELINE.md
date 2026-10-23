# 启动器冷启动基线（Launcher.Bench）

> 用途：后面动界面/美化时，用这份基线证明「没把启动速度搞慢」。
> 数字全部由 `tools/Launcher.Bench` 生成，可复现；口径见下。

- **采集时间**：2026-10-24 02:07（Asia/Shanghai）
- **目标产物**：`src/Launcher.App/bin/Release/net10.0-windows/MCLauncher.exe`
  （exe 162,304 B；实际逻辑在 `MCLauncher.dll` 212,992 B；构建于 2026-10-24 01:58:14；
  文件版本 `0.1.1.0`，产品版本 `0.1.2+c2078bd4368a0d0addce78764fb071152e686d3c`）
- **这是哪一版**：任务基线 HEAD `639bfa2` 对应的 Release 产物 —— 已含 JVM preset、启动前预检、编码 `-D` 参数、MemoryMb 自适应、natives 缓存/复用
- **数据目录**：`MCLAUNCHER_HOME=<launcher-data>`（固定数据目录，避免首启初始化差异）

## 机器

| 项 | 值 |
|---|---|
| CPU | AMD Ryzen 7 5700X 8-Core Processor |
| 内存 | 32,692 MB |
| 逻辑核 | 16 |
| 系统 | Microsoft Windows 10.0.26300 |
| .NET | 10.0.13 |

## 口径

- **handleMs**：`Process.Start` 起表 → 每 5 ms 轮询 `Process.MainWindowHandle != 0 && Responding`，首次为真的耗时
- **内存**：同一时刻的 `WorkingSet64` / `PrivateMemorySize64` / `PeakWorkingSet64`（MB，1 位小数）
- 每轮结束 `Kill(entireProcessTree: true)`，轮间隔 400 ms；每轮开始前先关掉同名残留实例（启动器有单实例互斥锁，不关就只会弹「已在运行」）
- 下面这组是**一次调用采 15 轮**，JSON 原文见 [BASELINE.json](BASELINE.json)

## 明细（15 轮）

| # | handleMs | WS MB | Private MB |
|---:|---:|---:|---:|
| 1 | 734 | 125.0 | 72.3 |
| 2 | 724 | 125.1 | 72.3 |
| 3 | 749 | 126.9 | 72.4 |
| 4 | 717 | 126.9 | 72.5 |
| 5 | 728 | 124.9 | 72.1 |
| 6 | 726 | 126.9 | 72.5 |
| 7 | 751 | 126.7 | 72.2 |
| 8 | 841 | 127.0 | 72.5 |
| 9 | 764 | 125.2 | 72.4 |
| 10 | 743 | 125.2 | 72.7 |
| 11 | 766 | 125.1 | 72.4 |
| 12 | 807 | 132.6 | 77.3 |
| 13 | 768 | 125.2 | 72.5 |
| 14 | 835 | 132.8 | 77.5 |
| 15 | 813 | 132.4 | 77.0 |

## 汇总

| 指标 | 中位数 | 最小 | 最大 | 平均 |
|---|---:|---:|---:|---:|
| handleMs | **751** | 717 | 841 | 764.4 |
| 内存 WS (MB) | **126.7** | 124.9 | 132.8 | — |
| 内存 Private (MB) | **72.5** | 72.1 | 77.5 | — |

参考：上一轮人工 pwsh 轮询得到的中位数是 **797 ms（723–945）**，量级一致。

## 批次间波动（同一天、同一产物，用来定「算不算变慢」）

| 批次 | 轮数 | 中位数 | 区间 |
|---|---:|---:|---|
| A | 5 | 805 | 728–828 |
| B | 5 | 735 | 715–769 |
| C | 5 | 796 | 753–830 |
| D | 15 | 751 | 717–841 |

→ 同一产物连续批次的中位数漂移可达 **±35 ms（≈5%）**。判定阈值建议：
**`|Δ| < 50 ms` 视为持平**，`|Δ| > 100 ms` 才算真变慢；跨批次比较时用 5 轮以上的中位数，别拿单轮比。

## 用法

```powershell
# 采一轮（5 次），JSON 落到指定路径
launcher-bench --exe src\Launcher.App\bin\Release\net10.0-windows\MCLauncher.exe --runs 5 --out bench.json

# 美化后：跑完自动和基线对比（打印中位数差值与百分比）
launcher-bench --exe src\Launcher.App\bin\Release\net10.0-windows\MCLauncher.exe --runs 5 `
  --baseline tools\Launcher.Bench\BASELINE.json --out bench-after.json
```

参数：`--exe`（必填）、`--runs`（默认 5）、`--out`（默认 `bench-<时间戳>.json`）、`--settle-ms`（默认 400）、`--timeout-ms`（默认 20000）、`--baseline`、`--help`。
退出码：`0` 全部成功 / `1` 有轮次失败 / `2` 参数或路径错误。

## 已知限制

- 只量到「主窗口可交互」，**不量首帧渲染**。`[boot] 首帧渲染完成` 仍在启动器日志里，要更细的分解得另抓日志。
- 内存有两个稳定档位（WS ≈125 MB 与 ≈132 MB），与 GC/ReadyToRun 状态有关 —— 看中位数，别单看某一轮。
- 被安全软件拦截时，工具原样打印 `Win32Exception`（含 `NativeErrorCode`）并以退出码 1 结束。
- 采样期间别同时编译：MSBuild 抢 CPU 会把中位数抬高几十毫秒。
- 单实例互斥锁意味着**测量会关掉正在运行的启动器**；工具只会动 `--exe` 对应的进程名。
