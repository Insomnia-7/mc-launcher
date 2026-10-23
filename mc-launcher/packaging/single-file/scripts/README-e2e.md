# 复现：单文件构建 + 首帧测量 + 端到端自更新

全部步骤在本机实测过（Windows 11 / .NET SDK 10.0.402 / win-x64）。耗时约 3–5 分钟（不含首次还原）。

---

## 0. 准备隔离副本

仓库里跑着的 `MCLauncher.exe` 会锁 `bin\...\net10.0-windows\*.dll`，就地发布会被 `MSB3027` 打断。
所以先复制一份到仓库外：

```powershell
$work = 'D:\work\_singlefile-test'      # 换成你自己的临时目录
robocopy D:\work\deepseek-harness\default-workspace\mc-launcher $work\src /E /XD bin obj .git /XF *.user
```

> `dotnet` 的路径：本机 SDK 10 在 `D:\dotnet\dotnet.exe`，PATH 里的 `dotnet` 是 6.x，
> 直接跑会 `NETSDK1045`。本目录的脚本都接受 `-Dotnet` 参数覆盖。

## 1. 打补丁

把 `drafts/src-patch.md` 里的三处 csproj 与一处 `MainViewModel` 改动应用到 `$work\src`。
不打算手改的话，至少要确认这两点：

- `Launcher.App.csproj` 的 `ProjectReference`（更新器那条）带 `Condition="'$(PublishSingleFile)' != 'true'"`；
- `Launcher.App.csproj` 里有 `EmbeddedResource Include="$(UpdaterPayload)" LogicalName="Launcher.Updater.mclupd.exe"`。

## 2. 构建

```powershell
cd mc-launcher\packaging\single-file\scripts
.\build-single-file.ps1 -Flavor all -WorkDir $work -Version 0.1.2 -Dotnet D:\dotnet\dotnet.exe
```

预期（本机实测，热构建）：

| 形态 | 文件数 | 总体积 |
|---|---|---|
| `fd` | 1 | 1.78 MB（不含内嵌更新器） |
| `fd-embedded` | **1** | **2.39 MB** |
| `sc` | 1 | 126.94 MB |
| `sc-compressed` | 1 | 59.52 MB |
| `sc-embedded` | 1 | 95.68 MB |

## 3. 首帧

```powershell
.\measure-launch.ps1 -Exe $work\out\fd-embedded\MCLauncher.exe,$work\out\sc-compressed\MCLauncher.exe -Runs 5
# 冷启动（清 %TEMP%\.net\MCLauncher 解压缓存）：
.\measure-launch.ps1 -Exe $work\out\sc-compressed\MCLauncher.exe -Runs 3 -Cold
```

参考值：`fd-embedded` 764 ms / `sc-compressed` 928 ms（热）、1037 ms（冷）/ `fd` 多文件现状 773 ms。

## 4. 端到端自更新

### 4.1 生成一把测试签名密钥

`update.json` 必须带 `rsa-pss-sha256` 签名，客户端才肯更新。公钥要写进**隔离副本**的
`UpdateTrustKeys.json`（不要动仓库里的那份）：

```powershell
.\sign-manifest.ps1 -GenKey `
  -KeysDir $work\keys `
  -TrustKeysPath $work\src\src\Launcher.Application\Update\UpdateTrustKeys.json
```

会打印 `keyId`（= SPKI 的 SHA-256 hex）并写好信任文件。改完 `UpdateTrustKeys.json` 后，
**必须重新构建主程序**，否则主程序里编进去的还是旧公钥。

### 4.2 造两个版本

```powershell
# 旧版 0.1.2
.\build-single-file.ps1 -Flavor sc -WorkDir $work -Version 0.1.2 -Dotnet D:\dotnet\dotnet.exe
# 新版 0.1.3（同一套源码，只改版本号）
.\build-single-file.ps1 -Flavor sc -WorkDir $work -Version 0.1.3 -Dotnet D:\dotnet\dotnet.exe
```

`build-single-file.ps1` 每次都会覆盖 `out\sc`，所以两次之间把产物挪走或改名。

### 4.3 构建 probe-embed（模拟「更新器内嵌在主程序里」）

```powershell
D:\dotnet\dotnet.exe publish .\probe-embed\probe-embed.csproj -c Release `
  -p:UpdaterPayload=$work\payload\updater-sc-compressed\mclupd.exe `
  -o $work\out\probe-embed
```

### 4.4 跑

```powershell
.\e2e-self-update.ps1 `
  -OldExe $work\out\sc-012\MCLauncher.exe `
  -NewExe $work\out\sc-013\MCLauncher.exe `
  -Updater $work\payload\updater-sc-compressed\mclupd.exe `
  -ProbeEmbed $work\out\probe-embed\probe-embed.exe `
  -WorkDir $work\e2e -KeysDir $work\keys
```

脚本会起两次真实进程，自动杀掉旧版模拟「主程序自己退出」，然后检查：

- 更新器退出码 = 0；
- 替换后 `sha256` 等于新版、文件版本 = 0.1.3；
- `.backup\<时间戳>\MCLauncher.exe` 的 `sha256` 等于旧版；
- `updater.log` 里有「备份 / 替换 / 账本已更新 / 完成」；
- 重启后的进程能起窗口。

实测输出（自包含形态，126.94 MB）：

```
阶段 1  独立 mclupd.exe     → 退出码 0，hash 匹配新版，重启后起窗口 = True
阶段 2  probe-embed 解到 %TEMP% → 退出码 0，hash 匹配新版，重启后起窗口 = True
```

## 5. 常见坑

| 现象 | 原因 | 处理 |
|---|---|---|
| `NETSDK1151` | 没给 `ProjectReference` 加 `PublishSingleFile` 条件 | 见 `drafts/src-patch.md` §1.1 |
| 单文件起不来、无窗口 | 缺 `-p:IncludeNativeLibrariesForSelfExtract=true` | WPF 有原生依赖，必须带 |
| `out\` 里除了 exe 还有 4 个 pdb | 没关调试符号 | 加 `-p:DebugType=none -p:DebugSymbols=false` |
| 更新器报「签名无效」/ 拒绝更新 | 信任文件没重新构建进主程序，或 `update.json` 没签 | 重建主程序；`sign-manifest.ps1 -Sign` |
| 替换时 `UnauthorizedAccessException` | 主程序没退出（`--wait-pid` 没生效/进程没退） | 确认 `--wait-pid <pid>` 传的是真 PID |
| `dotnet` 报 `NETSDK1045` | 用到了 PATH 里的 6.x | 用 `-Dotnet D:\dotnet\dotnet.exe` |
