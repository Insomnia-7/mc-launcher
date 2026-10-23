# 发布与自更新说明（release pipeline）

这套东西只做一件事：**把一个 tag 变成「客户端点一下就能升级」的 GitHub Release**。

```
push tag v0.3.0
   └─ .github/workflows/release.yml
        ├─ dotnet publish mc-launcher/src/Launcher.App        → out/
        ├─ dotnet publish mc-launcher/tools/Launcher.Updater  → out/updater/（单文件 mclupd.exe）
        ├─ 检查 out/** 里没有重名文件（Release 资产名是拍平的）
        ├─ pwsh scripts/make-update-manifest.ps1 -Directory out … → out/update.json
        ├─ 按更新器的规矩自检 update.json
        └─ softprops/action-gh-release 上传 out/** 作为 Release 资产
```

两个发布都用 `-p:DebugType=None -p:DebugSymbols=false`，**不产 pdb**：清单本来就排除调试符号，
也没必要把它们当 Release 资产传上去。

更新器必须是**单文件发布**（`-r win-x64 --self-contained false -p:PublishSingleFile=true`）：
普通的框架依赖发布会把 `Launcher.Application.dll` / `Launcher.Domain.dll` 再复制一份到 `out/updater/`，
而 GitHub Release 的资产名是拍平的，同名资产上传会 422 撞车。单文件后 `out/updater/` 里只有一个
`mclupd.exe`（~450 KB，依赖机器上的 .NET 10 运行时，和主程序一样是框架依赖）。

产出的 `update.json` 就是 `mclupd` 和客户端自更新流程唯一认的清单，形状与
`Launcher.Application.Update.UpdateManifestParser` 严格对齐，**不要手工改字段名**：

```json
{
  "version": "0.3.0",
  "notes": "更新说明或 null",
  "publishedAt": "2026-10-23T10:00:00+08:00",
  "files": [
    { "path": "MCLauncher.dll", "url": "https://…/MCLauncher.dll", "sha256": "<64 位小写 hex>", "size": 12345 }
  ]
}
```

- `path`：相对启动器安装目录，`/` 分隔。不允许 `..`、不允许绝对路径、不允许出现冒号 —— 更新器见到就整批拒收。
- `url`：可以是 http(s)，也可以是**相对清单所在目录**的路径（本地目录 / U 盘更新靠这个）。
- `sha256`：小写 64 位；`size`：字节数。

## 三步用法

### 1. 发版：push 一个 tag

```powershell
# 版本号自己定，tag 必须以 v 开头（工作流只认 v*）
git tag v0.3.0
git push origin v0.3.0
```

工作流会自己跑完发布。产物在 `Releases` 页面上：一堆 `*.dll` / `MCLauncher.exe` / `mclupd.exe` / `update.json`。

> 想手工重跑：Actions → Release → Run workflow，`tag` 填**已经存在**的 tag（例如 `v0.3.0`），
> `notes` 可留空。手工触发不会替你创建 tag。

### 2. 本地出清单（不经过 CI）

```powershell
# 先把发布目录准备好，例如 dotnet publish … -o D:\tmp\out
pwsh -File scripts/make-update-manifest.ps1 `
    -Directory D:\tmp\out `
    -Version 0.3.0 `
    -Notes "首个测试版" `
    -OutFile D:\tmp\out\update.json
```

参数：

| 参数 | 必填 | 说明 |
| --- | --- | --- |
| `-Directory` | 是 | 已发布目录，递归收录 |
| `-Version` | 是 | 版本号，`0.3.0` / `v0.3.0` 都行；空则报错 exit 1 |
| `-Notes` | 否 | 更新说明，不填写 `null` |
| `-BaseUrl` | 否 | 下载基地址，例如 `https://example.com/dl`；给了就拼绝对 url，不给写相对路径 |
| `-OutFile` | 否 | 默认 `<Directory>/update.json` |
| `-PublishedAt` | 否 | 固定发布时间，默认当前时间；需要「两次跑出字节一致的 JSON」时用它 |
| `-FlattenAssetUrls` | 否 | 只在配 `-BaseUrl` 时用：url 用文件名而不是相对路径（GitHub Release 资产名是拍平的，CI 必须带） |

行为细节：

- **排除**：`*.pdb`、任意 `update.json`、`.backup/` 目录下的所有文件、`*.log`，以及 `-OutFile` 自身。
- **排序**：按 `path` 的 Ordinal 升序，跨机器、跨语言环境一致。
- **输出**：UTF-8 无 BOM、2 空格缩进、LF 换行；同一份输入 + 同一个 `-PublishedAt` ⇒ 字节一致的 JSON。
- **报错**：目录不存在 / 是文件而不是目录 / 目录里没有可收录的文件 / `-Version` 为空 / `-BaseUrl` 不是 http(s) / `-PublishedAt` 非法 —— 全部一行中文说明 + **exit 1**，不抛栈。
- 脚本是 UTF-8 **带 BOM** 保存的，`pwsh` 7 和 Windows PowerShell 5.1 都能正确读中文。

### 3. 客户端指向清单

两种方式，环境变量优先：

```powershell
# 方式 A：环境变量（临时/调试首选）
$env:MCLAUNCHER_UPDATE_URL = "https://github.com/<owner>/<repo>/releases/latest/download/update.json"
```

```jsonc
// 方式 B：%APPDATA%\MCLauncher\settings.json
{
  "updateUrl": "https://github.com/<owner>/<repo>/releases/latest/download/update.json"
}
```

`releases/latest/download/update.json` 永远指向最新一次 Release 的清单，客户端不用跟着版本改地址。

本地目录也能当更新源（离线/内网/U 盘）：

```powershell
$env:MCLAUNCHER_UPDATE_URL = "D:\share\mc-launcher\"   # 目录 → 自动找里面的 update.json
```

## 更新器（mclupd）会怎么处理这份清单

1. **全量校验 staging**：清单里任何一个文件缺失、大小不符、sha256 不符 → **整批不动，exit 2**。
   半套文件比旧版更糟，所以这里是「一个不对就全退」。
2. **备份**：把要被替换的文件复制到 `<启动器目录>\.backup\<yyyyMMdd-HHmmss>\`（顺带写 `updater.log`）。
3. **替换**：目标文件被占用时重试 5 轮（间隔递增），仍失败 → 已替换的回滚并 **exit 3**。
4. **重启**：`--restart` 指定的程序（一般是 `MCLauncher.exe`）。

| 退出码 | 含义 |
| --- | --- |
| 0 | 成功 |
| 1 | 参数不对 / 未预期异常 |
| 2 | 没有 `update.json`、缺文件、大小不符、sha256 不符（整批未动） |
| 3 | 替换失败（文件被占用等） |
| 4 | 等主进程退出超时 |

## GitHub Release 的坑：资产名会被拍平

GitHub Release **不支持目录结构**：`out/updater/mclupd.exe` 上传后资产名是 `mclupd.exe`。
所以 CI 里生成清单时带了 `-FlattenAssetUrls`，`url` 取文件名而不是 `updater/mclupd.exe`；
`path` 仍然是 `updater/mclupd.exe`（客户端解压/替换要认这个路径）。

拍平还有第二个后果：**`out/**` 里不能有两个同名文件**，否则两个资产互相覆盖、或者上传直接 422。
更新器如果按多文件发布，`out/updater/` 里就会再出现一份 `Launcher.Application.dll` / `Launcher.Domain.dll`，
正好和 `out/` 根上的重名 —— 这就是更新器必须单文件发布的原因。workflow 里另外有一步
「检查资产名唯一」，真出现重名会当场 fail，不会传到一半才炸。

换成保留目录的静态托管（nginx / S3 / 内网目录）时**别带** `-FlattenAssetUrls`，否则子目录文件会 404。

另外 GitHub 会清洗资产名里的特殊字符（空格、中文等）。发布目录里尽量别放这类文件名，本项目产物都是 ASCII，没问题。

## 本地验证（改脚本后至少跑一遍）

```powershell
# 造假目录
New-Item -ItemType Directory -Force D:\tmp-ci-manifest\out\sub, D:\tmp-ci-manifest\out\中文目录 | Out-Null
Set-Content D:\tmp-ci-manifest\out\a.txt 'alpha'
Set-Content D:\tmp-ci-manifest\out\sub\b.dll 'beta'
Set-Content 'D:\tmp-ci-manifest\out\中文目录\说明 文档.txt' '中文'
Set-Content D:\tmp-ci-manifest\out\x.pdb 'debug'   # 应被排除

pwsh -File scripts/make-update-manifest.ps1 `
    -Directory D:\tmp-ci-manifest\out -Version 0.3.0 -Notes "测试" `
    -BaseUrl https://example.com/dl -OutFile D:\tmp-ci-manifest\out\update.json

# 回读检查
$m = Get-Content D:\tmp-ci-manifest\out\update.json -Raw | ConvertFrom-Json
$m.files.Count                      # 期望 3（pdb 被排除）
$m.files[2].url                      # 期望中文目录/说明 文档.txt 的百分号编码
Get-FileHash D:\tmp-ci-manifest\out\a.txt -Algorithm SHA256   # 与 files[0].sha256 比
```

想把更新器按 CI 的方式（单文件）发布出来手工试：

```powershell
dotnet publish mc-launcher/tools/Launcher.Updater -c Release `
    -o D:\tmp-ci-manifest\out\updater -r win-x64 --self-contained false -p:PublishSingleFile=true
# 然后用本地目录当更新源，验证清单能被真正应用：
mclupd --staging <有 update.json 的目录> --target <启动器目录>
```

## 已知限制

- 这套 workflow **没有在 GitHub 上真跑过**（本机没有 Actions 环境）。本地把 6 个 `run` 步骤按顺序原样
  复刻跑过一遍（真 `dotnet publish` 两条 + 真生成清单 + 真校验，全部退出码 0），Actions 侧还剩
  `actions/checkout`、`actions/setup-dotnet`、`softprops/action-gh-release` 三个现成 action 没执行过；
  首次发 tag 时留意日志。
- Release 资产名拍平是 GitHub 的行为，不是这套脚本的选择；换托管方式时记得去掉 `-FlattenAssetUrls`。
- 清单不含 `entry` 字段：重启目标由客户端传 `--restart`（默认 `MCLauncher.exe`）决定。
- 更新器只做「替换 + 重启」，不处理删除旧文件（例如某个 dll 在新版本里被删掉，它会留在安装目录里）。
- CI 出的 `mclupd.exe` 是单文件包，本地 `dotnet build` 出来的是多文件（`mclupd.exe` + 若干 dll）。
  两者都能用；更新后旧的 `updater\*.dll` 不会被删掉，但单文件包不会去读它们，不影响运行。
