# 单文件发布：实测、取舍与推荐方案

> 后续状态：方案 B 已接入源码、正式发布流程及本地脚本；当前入口和新一轮验证见 [落地记录](../../docs/single-file-release.md)。以下保留为此前的方案实验，drafts 中的补丁不再作为当前操作步骤。

> 目的：回答「主程序能不能只发一个 exe」。
> 结论：**能**，而且自更新链路在单文件下照样工作（已端到端实测）。
> 但「单文件」有两种完全不同的东西，体积差 50 倍，选错方向比不做更糟。
>
> 本文所有数字都来自本机实测（Windows 11 / .NET SDK 10.0.402 / win-x64 / Release），
> 原始工程是仓库 `HEAD 1bdf8e9` 的隔离副本，产物在 `<test-root>\out\`。
> 复现脚本见 `scripts/`，可直接套用的改动见 `drafts/`。

---

## 0. 先分清两种「单文件」

| | 框架依赖单文件 | 自包含单文件 |
|---|---|---|
| 目标机需要 .NET 10 Desktop Runtime | 需要 | 不需要 |
| 体积 | **2.39 MB** | **59.5 MB**（压缩）/ **127 MB**（不压缩） |
| 首帧 | 764 ms（与现状持平） | 902–933 ms（+130~160 ms） |
| 适合 | 现在的发布方式（本来就要运行时） | 面向「没装运行时」的用户 |

本仓库**现在发的就是框架依赖**（`release.yml` 里 `--self-contained false`，18 个文件 2.36 MB）。
所以对「只发一个 exe」这个诉求，**框架依赖单文件是零成本升级**：总体积 2.36 → 2.39 MB，
启动不变，前置依赖不变。

---

## 1. 单文件真正的坑不是体积，是更新器

现状是多文件布局，更新器在 `out\updater\mclupd.exe`，由 `Launcher.App.csproj` 的 `CopyUpdater` 目标拷过去：

```
out\MCLauncher.exe
out\updater\mclupd.exe      ← 主程序点「更新」时启动它
```

`-p:PublishSingleFile=true` 之后 `out\` 里只剩一个 exe，`updater\` 目录不存在，于是：

```csharp
// MainViewModel.FindUpdater()（原始实现）
Path.Combine(baseDirectory, "updater", "mclupd.exe")   // 不存在
Path.Combine(baseDirectory, "mclupd.exe")              // 不存在
// → 返回 null → 更新按钮直接报「找不到更新器 mclupd.exe」
```

**这是单文件方案必须先解决的事**，否则发出去的新版永远无法自更新——比多文件更糟。

顺便排除一条看似自然的做法：让 `Launcher.App` 直接 `ProjectReference` 更新器项目。
**不可行**，`dotnet publish --self-contained false -p:PublishSingleFile=true` 会直接失败：

```
error NETSDK1151: 引用的项目“Launcher.Updater.csproj”是自包含的可执行文件。
非自包含可执行文件不能引用自包含可执行文件。
```

所以更新器必须以**产物**（exe 文件）身份进入主程序，而不是以**项目引用**身份。

> 顺带纠正两份旧结论：
> `mc-launcher-handoff-2026-10-23.md:190` 写着「App **不能**单文件，别照两条都单文件改」。
> 那句话的前半段（不加条件时 `NETSDK1151`）是对的，但「不能」不成立——给 `ProjectReference` 加
> `Condition="'$(PublishSingleFile)' != 'true'"` 之后单文件发布正常，实测产物 2.39 MB、能起窗口、
> 自更新端到端通过（§4）。
> `scripts/README-release.md:22-25` 说「更新器必须是单文件发布」——**这条在方案 B 下依然成立**
> （更新器仍是单文件产物，只是被打进主程序里），但同段的「App 走多文件」描述需要改。

---

## 2. 实测矩阵

同一份源码，只改发布参数（`-p:Version=0.1.2`，`-c Release -r win-x64`）：

| # | 方案 | 目录文件数 | 总体积 | 主程序 exe | 构建耗时* |
|---|---|---|---|---|---|
| A | 现状：多文件 · 框架依赖（17 根文件 + `updater\mclupd.exe`） | 18 | 2.36 MB | 0.15 MB | 3.1 s + 1.6 s |
| B0 | 框架依赖单文件（**不含**更新器，不可用） | 1 | 1.78 MB | 1.78 MB | 12.4 s |
| B | **框架依赖单文件 + 内嵌更新器**（推荐） | **1** | **2.39 MB** | **2.39 MB** | 7.1 s |
| C | 自包含单文件 | 1 | 126.94 MB | 126.94 MB | 2.9 s |
| D | 自包含单文件 + 压缩 | 5 → 1 | 59.81 → 59.52 MB | 59.52 MB | 7.9 s |
| E | 自包含单文件 + 压缩 + 内嵌更新器 | 1 | 95.68 MB | 95.68 MB | 10.4 s |

A 的两段耗时 = 更新器 publish + 主程序 publish（CI 里就是两条独立命令，`release.yml:94-112`）。
B0 列出来是为了标价：内嵌更新器让主程序从 1.78 MB 涨到 2.39 MB（+0.61 MB），
而 B0 本身**不可用**（`FindUpdater()` 找不到任何更新器，更新按钮永久失效）。
注意 B 和 A 的**总体积几乎一样**（2.39 vs 2.36 MB）：省下的 17 个分散文件差不多抵掉了内嵌的更新器。

\* 构建耗时是单次热构建（`obj/` 已有），只用来横向比较量级，不代表 CI 冷构建。
CI 冷构建实测（`D:\ci-dryrun\REPORT.md`）：两条 publish 合计约 6.7 s。

单文件相关的附属产物：

| 产物 | 说明 | 体积 |
|---|---|---|
| 更新器 · 框架依赖单文件 | 给 B 内嵌用 | **0.61 MB** |
| 更新器 · 自包含单文件 | 独立用 | 70.59 MB |
| 更新器 · 自包含单文件 + 压缩 | 给 E 内嵌用 | **36.04 MB** |

**注意 D/E 的 pdb 问题**：默认发布带 4 个 `.pdb`（`MCLauncher.pdb`、`Launcher.Application.pdb`…），
`PublishSingleFile` 只合并程序集、不合并符号，于是「单文件」旁边仍会躺 4 个 pdb。
发布流水线必须显式 `-p:DebugType=none -p:DebugSymbols=false`，否则「一个 exe」是假象。
B/C/E 的数字都已经是关掉 pdb 的。

---

## 3. 首帧：单文件到底慢多少

测量方法：`Start-Process` 起进程 → 轮询 `MainWindowHandle != 0` 记为「首帧」，每形态 5 次，
每次结束后杀进程、等 250 ms 再跑下一次。窗口出现即计（不等到可交互）。

| 形态 | 中位数 | 最小 | 最大 | 冷启动（清空 %TEMP% 解压缓存后第一次） |
|---|---|---|---|---|
| A 现状（多文件 · FD） | **773 ms** | 749 | 802 | — |
| B FD 单文件 + 内嵌更新器 | **764 ms** | 747 | 826 | — |
| C SC 单文件 | 933 ms | 856 | 1019 | 858 ms |
| D SC 单文件 + 压缩 | 928 ms | 903 | 977 | **1037 ms** |
| E SC 单文件 + 压缩 + 内嵌更新器 | 902 ms | 867 | 965 | — |

结论：

- **框架依赖单文件没有可测量的启动代价**（764 vs 773，差异在噪声内）。内嵌 0.61 MB 更新器也不影响。
- 自包含单文件稳定慢 **130~160 ms**：它每次启动都要把 100+ MB 运行时解到 `%TEMP%\.net\<...>`。
- 压缩版**热启动和未压缩基本一样**（928 vs 933），代价在**首次**解压：冷启动多约 180 ms；
  之后复用 `%TEMP%` 缓存。所以「压缩让启动变慢」是错觉，它只让第一次慢。
- 这些数字都是「窗口出现」而不是「可交互」。若按可交互计，SC 的差距会更大（解压发生在进程启动早期）。

---

## 4. 自更新在单文件下还能不能工作：端到端实测

理论上没有障碍，因为 `mclupd` 的设计本来就是「在**另一个进程**里替换正在运行的程序」：

- `UpdateFileTransaction.ApplyAsync` 会**跳过 `runningExecutable`**（自己不能替换自己）；
- 主程序 `MainViewModel.ApplyUpdateAsync` 起更新器时带 `--wait-pid <自己的 PID>`，然后 `Application.Current.Shutdown()`；
- `mclupd` 先校验清单与签名，再 `Process.GetProcessById(pid).WaitForExitAsync()`（3 分钟超时），
  退出后 `File.Copy` 到 `目标.mcl-update-<guid>` → `File.Move(overwrite: true)`（5 次重试，300 ms 递增），
  备份到 `.backup\<stamp>\`，更新账本 `installed-files.json`，最后 `Process.Start` 重启主程序。

单文件唯一的变化是：**exe 自己就是整个应用**，替换它 = 替换全部。这反而更简单（清单只有 1 条），
而且从多文件迁到单文件时，旧目录里的那 17 个 dll/pdb 会被更新器的「上游已移除文件」清理
自动搬进 `.backup\<stamp>\`（见 §12.2），不需要人工清目录。

实测环境：`<test-root>\e2e\`，旧版 0.1.2（126.94 MB 自包含单文件）→ 新版 0.1.3，
清单用**自签测试密钥**签（`rsa-pss-sha256`），更新器是 36.04 MB 自包含压缩单文件。

### 阶段 1：独立 `mclupd.exe`（等价于现状多文件布局的链路，用在单文件 exe 上）

```
旧版 exe : bf2e0652…  126.94 MB
新版 exe : 677b183e…  126.94 MB
已安装版本运行中 pid=21940；此时 exe 被占用
更新器 pid=28076 仍在等待 = True（主程序还活着 = True）
等待期间 exe hash 未变 = True
主程序已退出（真实流程是 Application.Current.Shutdown()）
更新器退出码 = 0
  [out] 等待进程 21940 退出…
  [out] 准备更新到 0.1.3（1 个文件）
  [out] 更新完成，备份在 …\.backup\20261024-021506-732-cde244cd
  [out] 重启 …\MCLauncher.exe
替换后 hash = 677b183e…   匹配新版 = True；文件版本 = 0.1.3
备份里的旧 exe hash 匹配旧版 = True
  [log] 备份 MCLauncher.exe
  [log] 替换 MCLauncher.exe
  [log] 账本已更新：1 个文件
  [log] 完成
重启后起窗口 = True
```

### 阶段 2：更新器嵌在主程序里，更新时才解到 `%TEMP%`

用 `probe-embed`（与 `drafts/src-patch.md` 里那段 `ExtractEmbeddedUpdater` 完全相同的逻辑）复现：

```
  [out] [probe] 嵌入资源 36.0 MB → C:\Users\…\AppData\Local\Temp\mclauncher-updater\0.1.2.0-37789493\mclupd.exe
  [out] 等待进程 36784 退出…
  [out] 更新完成，备份在 …\.backup\20261024-021514-779-816296cb
  [out] 重启 …\MCLauncher.exe
  [out] [probe] mclupd 退出码 0
probe 退出码 = 0
替换后 hash = 677b183e…   匹配新版 = True；文件版本 = 0.1.3
重启后起窗口 = True
```

**结论**：单文件不破坏自更新。前提只有两条，都必须写进实现：

1. 更新器必须在**主程序之外**运行（现状就是，靠 `--wait-pid` 等主程序退出）；
2. 单文件布局下更新器必须能**被找到**——内嵌 + 解到 `%TEMP%` 是本次验证过的做法。

解压目录名带「程序集版本 + 资源字节数」（`0.1.2.0-37789493`），升级后自然换一份，
不会复用旧版本留下的更新器；同名同长度则直接复用，避免每次更新都重写 36 MB。

### Defender

三个产物（2.39 MB / 95.68 MB / 59.52 MB）本机 Defender 全量扫描：`found no threats`。
未签名 + 低分发量的 SmartScreen 声誉问题**没有测**，它跟单文件无关，跟签名和分发量有关。

---

## 5. 推荐：方案 B（框架依赖单文件 + 内嵌更新器）

**理由**（按重要性排）：

1. **不新增前置依赖**。产品现在就是框架依赖发布，用户机器上已经有 .NET 10 Desktop Runtime；
   换成自包含只是把运行时复制进 exe，对现有用户是纯浪费。
2. **体积几乎不变**：2.36 MB → 2.39 MB（+0.03 MB：内嵌 0.61 MB 更新器，省掉 17 个分散文件）。
3. **启动无代价**：764 ms vs 现状 773 ms。
4. **CI 改动最小**：`release.yml` 两条 publish 各加几个 `-p:`，产物名从目录变一个文件。
5. 自更新已实测通过（§4）。

改动共 4 处，完整代码见 `drafts/src-patch.md`：

| 文件 | 改动 |
|---|---|
| `src/Launcher.App/Launcher.App.csproj` | `ProjectReference` 加 `Condition="'$(PublishSingleFile)' != 'true'"`；`CopyUpdater` 同条件；新增 `EmbeddedResource` |
| `src/Launcher.App/ViewModels/MainViewModel.cs` | `FindUpdater()` 找不到时回落到 `ExtractEmbeddedUpdater()`；新增该方法 |
| `.github/workflows/release.yml` | 先发更新器，再带 `-p:UpdaterPayload=` 发主程序 |
| `packaging/winget/` | 清单从 `zip` 改成 `portable`（可选，见 §7） |

构建命令（就是 CI 里那两条，本地可直接跑）：

```powershell
# 1) 更新器：框架依赖单文件，0.61 MB（放 out/ 之外，别让它进更新清单）
dotnet publish mc-launcher/tools/Launcher.Updater/Launcher.Updater.csproj -c Release -r win-x64 `
  --self-contained false -p:Version=0.1.2 `
  -p:PublishSingleFile=true -p:DebugType=none -p:DebugSymbols=false `
  -o payload/updater

# 2) 主程序：框架依赖单文件 + 内嵌更新器，2.39 MB
dotnet publish mc-launcher/src/Launcher.App/Launcher.App.csproj -c Release -r win-x64 `
  --self-contained false -p:Version=0.1.2 `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=none -p:DebugSymbols=false `
  -p:UpdaterPayload=payload/updater/mclupd.exe `
  -o out
```

`scripts/build-single-file.ps1` 是上面两条的封装：`-Flavor fd-embedded` 就是方案 B，
`-Flavor sc-embedded` 是方案 E，`-Flavor all` 一次跑完全部形态。

### 代价与风险

- 主程序每个版本都多带 0.61 MB 更新器；更新器自身升级需要「随主程序一起发」，不能单独热更
  （现状也一样，`updater\mclupd.exe` 同样是随包发的）。
- `IncludeNativeLibrariesForSelfExtract=true` 是必需的：WPF 有原生依赖（`wpfgfx_cor3.dll` 等），
  不加这个参数单文件起不来（方案 B 的构建参数里已包含）。
- 解压到 `%TEMP%` 意味着**用户清空临时目录不影响已装程序**，但会让下次更新多花一次 0.6 MB 的解压（可忽略）。

---

## 6. 备选：方案 E（自包含单文件 + 压缩 + 内嵌更新器）

只在「目标机可能没有 .NET 10 Desktop Runtime」时才值得。三个自包含形态里选 E：

| 形态 | 体积 | 说明 |
|---|---|---|
| C 不压缩 | 126.94 MB | 下载慢，但冷启动最快（858 ms） |
| D 压缩 | 59.52 MB | 体积砍半，冷启动 +180 ms |
| **E 压缩 + 内嵌更新器** | **95.68 MB** | 仍然是 1 个文件，零依赖，自更新可用 |

E 比 D 多 36 MB，因为内嵌的更新器自己也得是自包含（否则目标机没运行时，更新器起不来）。
如果坚持要小，可以**不内嵌**，把 36 MB 的 `mclupd.exe` 作为第二个资产单独发——
但那就不再是「一个 exe」了，回到多文件布局，`updater\` 目录又得存在。

**自包含的隐性成本**：每次启动解压运行时（130~160 ms）、每次更新下载 60~127 MB、
CI 产物大 25~50 倍、winget 校验与 CDN 缓存都更重。现有用户并没有运行时缺失问题，
所以不建议为「看起来更省心」付这个代价。

---

## 7. 不推荐的两条路

1. **框架依赖单文件、但不内嵌更新器**：`FindUpdater()` 返回 null，更新按钮永久失效，
   错误文案还写着「应该在安装目录的 updater\ 下」——对用户是死路。
2. **`ProjectReference` 直接引用更新器项目**：`NETSDK1151`，编译期就失败（§1）。

---

## 8. 对发布流水线的影响（草案见 `drafts/release-yml.md`）

现状（`release.yml`）发的是目录 → 打 zip → 生成 `update.json` → 签名 → 上传。
改单文件后必须动的地方（顺序有依赖）：

1. **publish 顺序**：先更新器到 `payload/updater/`（**必须在 `out/` 之外**，否则清单会把它也扫成一个要替换的文件），
   再主程序（`-p:UpdaterPayload=payload/updater/mclupd.exe` 指向它）。顺序反了会静默不内嵌 → 更新按钮失效。
2. **`update.json` 的 `files[]`**：`path` 仍是 `MCLauncher.exe`（安装后的文件名），`url` 由 `-FlattenAssetUrls`
   拍平成资产名——`UpdateClient.StageAsync` 用 `file.Path` 决定暂存文件名、**不看 url 的文件名**
   （`UpdateClient.cs:74`），所以资产改名不会破坏更新。
3. **`校验 update.json` 步骤（`release.yml:267-269`）现在硬性要求 `updater/mclupd.exe`**，
   单文件清单里没有它 → 这一步必然 `throw`。必须改成要求主程序存在（保留老分支做兼容）。
4. **签名顺序**：更新器要在**内嵌之前**签，否则用户在 `%TEMP%` 里跑到的 `mclupd.exe` 不带 Authenticode 签名，
   Windows 首次执行会弹拦截。顺序应为：发布更新器 → 签 payload → 发布主程序（内嵌）→ 签主程序。
   签名步骤里 `@('out/MCLauncher.exe','out/updater/mclupd.exe') | Where-Object { Test-Path $_ }`
   在单文件下会自动只签主程序，不用改，但会漏掉 payload。
5. **`1MB` 体积守卫已经不存在了**：当前 `release.yml:217-221` 用的是「zip ≥ 源体积 25%」的比例守卫，
   实测单文件 exe 压成 zip 是 42.7%（1,072,276 B / 2,508,364 B），不会误触发。
   （`task-11` 报告里那条「`if ($size -lt 1MB) throw` 会误杀」针对的是更早的版本，现已修。）
6. **新增一条单文件产物守卫**：`out/` 里除 `MCLauncher.exe` 和 `update.json` 外不许有别的文件——
   它一次挡住「`PublishSingleFile` 没生效」「pdb 没关」「payload 误落进 out/」三类事故（草案里给了现成实现）。
7. **`检查资产名唯一` / `打包便携版 zip` / `发布到 GitHub Release` 都不用改**：单文件下只剩一个名字，
   `out/**` + `dist/*.zip` 的上传列表照旧。

## 9. winget（可选，草案见 `drafts/winget-portable.md`）

单文件之后清单可以更简单：`InstallerType: portable`，`InstallerUrl` 指向那个 exe，
`Commands: [mclauncher]`，不再需要 `NestedInstallerType` / `NestedInstallerFiles`。
但要注意：**portable 类型的卸载由 winget 自己管**（删 exe），而本程序有 `.backup\`、
`installed-files.json` 和用户数据目录，`portable` 不会清理这些。
所以稳妥做法是**继续发 zip + `zip` 清单**（`packaging/winget/` 现状即可用），
`portable` 作为后续可选项，不要为了「单文件」顺带改安装语义。

---

## 10. 复现步骤

```powershell
# 1) 隔离副本（仓库里的 MCLauncher.exe 会锁 bin，不能就地发布）
robocopy <repo>\mc-launcher <test-root>\src /E /XD bin obj .git /XF *.user

# 2) 打补丁（drafts/src-patch.md 里的三处 csproj + 一处 MainViewModel）

# 3) 构建矩阵（A~E 全跑一遍，约 1 分钟；产物落 <WorkDir>\out\<flavor>\）
.\scripts\build-single-file.ps1 -Flavor all -WorkDir <test-root> -Dotnet <dotnet-sdk>\dotnet.exe

# 4) 首帧测量
.\scripts\measure-launch.ps1 -Exe <某个产物>\MCLauncher.exe -Runs 5

# 5) 端到端自更新（需要一对测试签名密钥，见下）
.\scripts\e2e-self-update.ps1 -OldExe <0.1.2 产物> -NewExe <0.1.3 产物> `
    -Updater <mclupd.exe> -ProbeEmbed <probe-embed.exe> -WorkDir <临时目录> -KeysDir <keys 目录>
```

> 脚本的 `-Dotnet` 默认是 `dotnet`；本机 PATH 里是 6.x，必须显式给 `<dotnet-sdk>\dotnet.exe`，
> 否则报 `NETSDK1045`。

自更新测试需要一个 `rsa-pss-sha256` 签名密钥对，且公钥要写进隔离副本的 `UpdateTrustKeys.json`
（**不要**改仓库里的信任配置）。生成命令见 `scripts/README-e2e.md`。

---

## 11. 未覆盖 / 已知边界

- **没有在真实 UI 上点过「更新」按钮**：本机无 UI 自动化，用「起更新器 + `--wait-pid` + kill 主程序」
  等价复现了退出路径。UI 侧的 `ApplyUpdateAsync` 只是把同样的参数传给 `Process.Start`，
  代码路径已逐行对照，但「按钮 → 下载 → 重启」这一整条没有鼠标级验证。
- **签名**：端到端用的是自签测试密钥，`release.yml` 的正式证书流程未参与。
- **SmartScreen**：未签名 exe 的信誉拦截没测，与单文件无关。
- **只测了 win-x64**。win-arm64 的运行时解压体积与首帧未测。
- **构建耗时**是热构建单次采样，只用于横向比较。
- 首帧口径是「主窗口句柄出现」，不是「可交互」。
- **「18 文件 → 1 文件」的迁移清理没有端到端跑过**：依据是代码路径（`Launcher.Updater/Program.cs:61-66`）
  与现成用例（`tests/Core.Checks/Program.cs:1725-1744` 覆盖了 `ComputeStale` 的删除/保留/无账本分支），
  本次 e2e 用的是 1 条文件的清单。

---

## 12. 回滚

分三层，**互不牵连**：

### 12.1 只回滚 CI（最常见）

`src/` 的补丁本身是**向后兼容**的：

- `CopyUpdater` 只在非单文件时才拷 `updater\`，多文件布局照旧；
- `FindUpdater()` 先找 `updater\mclupd.exe`、再找同目录 `mclupd.exe`、最后才解内嵌资源 ——
  只要目录里有更新器，内嵌那段根本不会执行。

所以把 `release.yml` 的两条 publish 参数改回去（去掉 `-p:PublishSingleFile` / `-p:UpdaterPayload` /
`-p:IncludeNativeLibrariesForSelfExtract`，恢复 `-o out` 目录发布），就回到今天的行为，
产物 21 个文件 + zip，`update.json` 重新变成 18 条。**不需要改代码，也不需要发新版本号之外的任何动作。**

### 12.2 已发布的单文件版本要退回多文件

两个方向都能走通，因为更新器**自带「上游已移除文件」清理**：

`Launcher.Updater/Program.cs:61-66` 每次更新前读账本 `installed-files.json`，
用 `InstalledFileSet.ComputeStale(previous, manifest.Files)` 算出「上版装过、这版清单里没有」的文件，
先移进 `.backup\<stamp>\` 再删除（只动账本记过的文件，用户自己放进目录的东西不碰）。

所以：

- **多文件老用户 → 单文件新版**：清单只剩 1 条，其余 17 个 `Launcher.*.dll` / `*.pdb` 会被自动清进 `.backup\`；
- **单文件 → 多文件回退**：清单回到 18 条，`mclupd` 按清单写回所有文件，账本随之更新。

两条路都不需要人工清目录。

### 12.3 更新失败就地回滚

`mclupd` 每次替换前把旧文件拷进 `.backup\<stamp>\`，并写 `updater.log`。手工回滚：

```powershell
$install = 'C:\Users\<you>\AppData\Local\Programs\MCLauncher'   # 换成实际安装目录
$stamp   = Get-ChildItem "$install\.backup" -Directory | Sort-Object Name -Descending | Select-Object -First 1
Copy-Item "$($stamp.FullName)\MCLauncher.exe" "$install\MCLauncher.exe" -Force
```

把旧 exe 拷回去就回到旧版；账本不用动（版本号只是记录，替换与否由清单决定）。
**不要删 `installed-files.json`**：删了之后 `ComputeStale` 拿不到上一版清单，
「上游已移除文件」就永远不会被清理，目录会越滚越脏。`.backup\` 本身不会被自动清理，
确认新版本没问题后可以手动删掉旧的时间戳目录。
