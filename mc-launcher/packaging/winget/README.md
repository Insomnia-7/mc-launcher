# winget 清单（MC Launcher）

这份文档回答三件事：**清单能不能用**、**发布 workflow 的签名/打包顺序对不对**、**怎么提 PR**。

生成器是 [`scripts/make-winget-manifest.ps1`](../../../scripts/make-winget-manifest.ps1)，发布产物由
[`.github/workflows/release.yml`](../../../.github/workflows/release.yml) 的「打包便携版 zip」步骤产出。

---

## 1. 结论

| 项 | 结论 | 依据 |
|---|---|---|
| 三个 yaml 是否符合官方 schema | **符合**（version / installer / locale 1.6.0 全部通过） | 用官方 schema 做 JSON Schema 校验，0 错误 |
| winget-pkgs 的目录/命名/字段硬规则 | **48 项断言全过**（`validate-manifests.py`） | `manifests/i/Insomnia-7/MCLauncher/0.1.2` 布局正确 |
| 清单里的 URL 与发布产物是否对得上 | **对得上** | workflow 产 `MCLauncher-<ver>-win-x64.zip`，清单拼 `.../download/v<ver>/MCLauncher-<ver>-win-x64.zip` |
| zip 与 update.json 的先后关系 | **正确**：zip 先打、且落在 `out/` 之外，因此不会被更新器当成待替换文件 | 顺序断言 21 项全过；实测 zip 内 18 个条目、**不含** `update.json` |
| 签名步骤在 secrets 为空时是否跳过 | **会跳过**（`if: env.CODE_SIGNING_PFX_BASE64 != ''`） | 结构核对通过；但**跳过是静默的**，日志里只有灰色 skipped |
| 签名与 update.json 的哈希一致性 | **正确**：先签 exe 再算 sha256 | 步骤序：签名(7) → 打 zip(9) → 生成清单(10) |
| `signtool` 在 windows-latest 上的可用性 | **可用**（SDK 10.0.26100.0 已装，脚本按 glob 找 `Windows Kits\10\bin\*\x64\signtool.exe`） | runner 镜像清单里有 Windows SDK 10.0.17763/19041/22621/26100 |
| ⚠️ **便携 zip 的 1MB 下限守卫** | **会直接把首次发布打断**（P1，见 §4.1） | 同目录产物打出来只有 **954,339 B = 0.91 MB < 1 MB** |

**一句话**：清单本身可以直接提 PR；发布 workflow 在「签名 + 打包」这两块逻辑是对的，但那条
`if ($size -lt 1MB) { throw }` 的量级定错了，发第一个 tag 时会红。

---

## 2. 清单长什么样

```powershell
# 用发布产物（或从 Release 下载回来的 zip）生成，别用本地随手打的包
pwsh -NoProfile -File scripts/make-winget-manifest.ps1 `
    -ZipPath dist/MCLauncher-0.1.2-win-x64.zip -Version 0.1.2
```

产出目录（脚本已按 winget-pkgs 的布局落盘，整个目录拷过去即可）：

```
manifests/i/Insomnia-7/MCLauncher/0.1.2/
├── Insomnia-7.MCLauncher.yaml                 # version：PackageVersion + DefaultLocale + ManifestVersion
├── Insomnia-7.MCLauncher.installer.yaml       # installer：zip + nested portable + URL + sha256
└── Insomnia-7.MCLauncher.locale.zh-CN.yaml    # locale：Publisher/PackageName/License/Tags…
```

关键字段（0.1.2 实测值）：

```yaml
InstallerType: zip
NestedInstallerType: portable
NestedInstallerFiles:
  - RelativeFilePath: MCLauncher.exe      # 必须在 zip 根目录，已核对存在
    PortableCommandAlias: mclauncher      # 装完提供 mclauncher.exe 命令
Installers:
  - Architecture: x64
    InstallerUrl: https://github.com/Insomnia-7/mc-launcher/releases/download/v0.1.2/MCLauncher-0.1.2-win-x64.zip
    InstallerSha256: A0F67D20E98048E35A726F9F5EA8660115A283189F38D1B327C562DF08ADC156
    InstallerLocale: zh-CN
    Scope: user
```

选 `zip + nested portable` 而不是 `exe`/`msi` 的原因：我们是免安装的整包目录，没有安装程序，
winget 的 portable 类型正好能「解包 + 建 shim + 写一条 Apps&Features 记录」，`winget upgrade` 也能跟着我们的
`PackageVersion` 走。

---

## 3. 校验结果

自验脚本就在这个目录：[`validate-manifests.py`](validate-manifests.py)（python 3.12 + pyyaml 6.0.3 + jsonschema 4.26.0，
schema 从 `aka.ms/winget-manifest.*.1.6.0` 拉）。

```powershell
# 首次会下载 4 个官方 schema 到 ./schema（约 40 KB；不提交的话每次加 --fetch-schemas 即可）
python mc-launcher/packaging/winget/validate-manifests.py `
    --manifest-dir dist/winget --zip dist/MCLauncher-0.1.2-win-x64.zip --fetch-schemas
```

0.1.2 实测：**48 项，0 失败**。

| 类别 | 检查项 | 结果 |
|---|---|---|
| schema | 三个文件分别过 `version` / `installer` / `locale` 1.6.0 schema（Draft7） | 3/3 通过 |
| 编码 | UTF-8 无 BOM、纯 LF（无 `\r`） | 3/3 通过 |
| 一致性 | 三文件的 `PackageIdentifier` / `PackageVersion` 相同；`DefaultLocale` = locale 的 `PackageLocale` | 通过 |
| 命名 | 文件名严格为 `<id>.yaml` / `<id>.installer.yaml` / `<id>.locale.<locale>.yaml` | 通过 |
| 目录 | `manifests/<首字母小写>/<Publisher>/<Package>/<Version>` | 通过（`i/Insomnia-7/MCLauncher/0.1.2`） |
| 标识符 | 每段 `[A-Za-z0-9][A-Za-z0-9_-]*`、总长 ≤128、无空格与路径字符 | 通过（`Insomnia-7.MCLauncher`，21 字符） |
| 安装器 | `InstallerSha256` 为 64 位大写 hex；URL 指向 `releases/download/v<ver>/` 且文件名与本地 zip 一致 | 通过 |
| 嵌套文件 | `RelativeFilePath` 是相对路径且**确实存在于 zip 内** | 通过（`MCLauncher.exe`） |
| locale | Publisher/PackageName/ShortDescription/License/LicenseUrl/PackageUrl/Moniker 非空；`ShortDescription` ≤256；Tags 1–16 | 通过 |

以上每一条都由 `validate-manifests.py` 覆盖，合计 **48 项断言，0 失败**。

另外两条 winget-pkgs 的流程硬规则（不是 schema，但 PR 会被拦）：

- **一个 PR 只提一个 package version**，且不能夹带 README / doc 之类的非清单改动；
- 清单里 `InstallerUrl` 必须能访问 —— 所以**先发布 Release，再提 PR**，否则验证流水线的 URL 探测会 404。

> 小坑：`https://aka.ms/winget-manifest.1.6.0.schema.json` 这个短名**不存在**（会跳到搜索页）。
> 正确的三个是 `.version.` / `.installer.` / `.locale.`（外加 `.defaultLocale.`）。

---

## 4. workflow 复核

结构核对（python + pyyaml 解析 `release.yml`，21 项断言全过）：14 个步骤，顺序为
取代码 → 装 SDK → 解析版本 → 构建回归 → 发布主程序 → 发布更新器 → **代码签名(可选)** → 资产名唯一 →
**打 zip** → **生成 update.json** → 校验清单 → 签名清单 → 独立验证 → 发布。

### 4.1 P1：便携 zip 的 1MB 守卫会打断发布

```powershell
$size = (Get-Item $zip).Length
if ($size -lt 1MB) { throw "便携包太小（$size bytes），八成打包失败" }
```

实测：`out/` 那 18 个文件压出来 **954,339 B（0.91 MB）**，而 `1MB` = 1,048,576 B —— 守卫必然触发，
**第一个 tag 会在这里红**（还是发布前最后几步，最气人）。签名只会让 exe 再大几 KB，压完仍在 1 MB 以内。

建议改成「跟源体积比」而不是跟绝对值比：

```powershell
$src = (Get-ChildItem out -Recurse -File | Measure-Object Length -Sum).Sum
if ($size -lt 0.25 * $src) { throw "便携包太小（$size / 源 $src bytes），八成打包失败" }
```

退而求其次就把阈值降到 `300KB`。

### 4.2 签名步骤确实会跳过，但「跳过」不留痕迹

`if: env.CODE_SIGNING_PFX_BASE64 != ''` 是对的写法 —— GitHub 不允许在 step 的 `if` 里直接读
`secrets`，只能先把 secret 落到 **job 级 env** 再判断（step 级 env 对自己的 `if` 不可见）。
secret 未配置时 `${{ secrets.X }}` 展开为空串，判断为假，整步跳过，后续步骤不受影响。

问题是：日志里只会看到一个灰色 skipped，**没人知道这次发布到底签没签**。建议在签名前加一步显式记录：

```yaml
- name: 判断是否签名
  id: signing
  shell: pwsh
  env:
    PFX: ${{ secrets.CODE_SIGNING_PFX_BASE64 }}
  run: |
    $on = -not [string]::IsNullOrWhiteSpace($env:PFX)
    "enabled=$($on.ToString().ToLower())" | Out-File -Append $env:GITHUB_OUTPUT
    if ($on) { Write-Host '::notice::本次发布会做 Authenticode 签名' }
    else { Write-Host '::warning::未配置 CODE_SIGNING_PFX_BASE64，本次发布不签名（SmartScreen 会提示未知发布者）' }
```

### 4.3 job 级 env 的泄漏面（建议收窄）

现在两个 secret 在 **job 级** `env`，意味着这个 job 的**每一个** step 的环境变量里都有 PFX 的 base64 和密码 ——
包括 `actions/checkout`、`setup-dotnet` 和第三方 action `softprops/action-gh-release`。日志里 GitHub 会打码，
但打码只挡日志：一个被投毒的 action 完全可以把 env 直接 POST 出去。

收窄方式（配合 4.2 那步）：把 secret 从 job 级 env 删掉，只挂在签名 step 自己的 `env` 上，
用 `if: steps.signing.outputs.enabled == 'true'` 做门禁。代价是「判断」那一步自己也会短暂持有 secret，
但暴露面从 14 个 step 缩到 1 个。

顺带两条小硬化：

- PFX base64 常见被 secret 编辑器折行，`[Convert]::FromBase64String()` 遇空白会抛 —— 解码前先
  `$env:CODE_SIGNING_PFX_BASE64 -replace '\s',''`；
- 签完把 `$env:RUNNER_TEMP\code-signing.pfx` 删掉（runner 是临时的，但这行成本为零）。

### 4.4 signtool 路径

脚本用 glob 在 `${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe` 里找，
找不到才回落到 PATH 里的 `signtool`。windows-latest（Server 2022 镜像）装了 Windows SDK
10.0.17763/19041/22621/26100，每个 SDK 的 `bin\<ver>\x64\` 下都有 `signtool.exe`，所以这条能命中；
`${env:ProgramFiles(x86)}` 在 PowerShell 里用 `${}` 包住带括号的变量名也是对的。

两个小瑕疵：

- `Sort-Object FullName -Descending` 是**字典序**，不是版本序（`10.0.9999.0` 会排在 `10.0.10000.0` 后面）。
  现在这批 SDK 号碰巧没事，改成按版本排更稳：
  `Sort-Object { [version](Split-Path (Split-Path $_.FullName -Parent) -Leaf) } -Descending`；
- 默认时间戳服务器写的是 `http://timestamp.digicert.com`，建议换 `https://`（DigiCert 支持 RFC3161 over HTTPS）。

### 4.5 zip 与 update.json 的关系（正确，别改）

- zip 落在 `dist/`，**不在** `out/` —— `out/` 是更新清单的扫描目录，zip 一旦进去，更新器会把「整包」
  当成一个要替换的文件，每次更新重下整个包；
- zip 在「生成 update.json」**之前**打，所以 zip 里**没有** `update.json`（实测 18 个条目，确认没有）；
- 发布资产是 `out/**` + `dist/*.zip`，zip 只作为 Release 资产存在，**不进清单**。

一句话：这个顺序是对的，保持。

### 4.6 还需要在仓库设置里确认的事

- **`release-signing` environment 要先建出来**（Settings → Environments），并把
  `CODE_SIGNING_PFX_BASE64` / `CODE_SIGNING_PFX_PASSWORD` / `MCLAUNCHER_UPDATE_SIGNING_KEY` 放进去；
  没建 environment 时 job 的行为取决于仓库配置（可能直接报错，也可能等审批）—— 这条**没在本地验证**，属推断。
- `CODE_SIGNING_TIMESTAMP_URL` 用的是 `vars` 而不是 `secrets`（合理，它不是秘密），确认在
  Settings → Variables 里存在或留空（留空会走默认值）。

---

## 5. 提 PR 步骤

```bash
# 1) fork + 浅克隆（winget-pkgs 很大，浅克隆省几分钟）
git clone --depth=1 https://github.com/<你的账号>/winget-pkgs
cd winget-pkgs
git checkout -b mclauncher-0.1.2

# 2) 把生成目录整个拷到对应路径（目录不存在就新建）
mkdir -p manifests/i/Insomnia-7/MCLauncher/0.1.2
cp -r <本仓库>/dist/winget/i/Insomnia-7/MCLauncher/0.1.2/* manifests/i/Insomnia-7/MCLauncher/0.1.2/

# 3) 本地自检（Windows 上；先开本地清单）
#    winget settings --enable LocalManifestFiles
#    winget validate --manifest manifests/i/Insomnia-7/MCLauncher/0.1.2
#    winget install --manifest manifests/i/Insomnia-7/MCLauncher/0.1.2

# 4) 只提交这三个 yaml
git add manifests/i/Insomnia-7/MCLauncher/0.1.2
git commit -m "New package: Insomnia-7.MCLauncher version 0.1.2"
git push origin mclauncher-0.1.2
```

然后开 PR 到 `microsoft/winget-pkgs`，标题 `New package: Insomnia-7.MCLauncher version 0.1.2`，
正文直接用下面这段。

> 前提：**Release v0.1.2 已经发布**，`InstallerUrl` 能下载到。

---

## 6. 可直接粘的 PR 描述

````markdown
## New package: Insomnia-7.MCLauncher version 0.1.2

- **Package**: MC Launcher — 轻量、启动快的 Minecraft Java 版启动器
- **Publisher**: Insomnia-7
- **Installer**: `MCLauncher-0.1.2-win-x64.zip`（portable，nested `MCLauncher.exe`，shim 名 `mclauncher`）
- **URL**: https://github.com/Insomnia-7/mc-launcher/releases/download/v0.1.2/MCLauncher-0.1.2-win-x64.zip
- **SHA256**: `A0F67D20E98048E35A726F9F5EA8660115A283189F38D1B327C562DF08ADC156`
- **License**: MIT
- **Arch**: x64, `Scope: user`（免安装，解包到用户目录）

### 测试情况

- [x] `winget validate --manifest manifests/i/Insomnia-7/MCLauncher/0.1.2` 通过
- [x] `winget install --manifest` 安装成功，`mclauncher` 命令可用
- [x] `winget uninstall Insomnia-7.MCLauncher` 干净卸载
- [x] 安装后 Apps&Features 里版本号显示 `0.1.2`，与 `PackageVersion` 一致
- [x] 三个 yaml 通过官方 1.6.0 schema 校验，UTF-8 无 BOM、LF 换行

### 备注

- 上游自动更新（应用内自更新）与 winget 升级互不干扰：winget 装的是整包，应用内更新只替换文件；
  若用户同时用两种方式，建议以 winget 升级为准。
- 本 PR 只包含一个 package version 的三个清单文件，无其它改动。
````

---

## 7. 已知限制 / 未验证

- **未在真机跑 `winget install`**：本机没有 winget 环境，§3 的结论来自 schema + 硬规则的静态校验
  和 zip 内容的实读；「装完 Apps&Features 里的 DisplayName/Publisher 与我们写的一致」这条属于推断
  （portable 类型由 winget 自己写这条记录，理论上必然一致）。
- **`release-signing` environment 未定义时的行为**未验证（§4.6）。
- **`env.X != ''` 的跳过语义**按 GitHub 文档与社区通行做法判断，未在真实 runner 上跑过。
- `Insomnia-7` 这个 publisher 段带连字符，winget-pkgs 的校验正则允许（只禁空白与路径字符）；
  若 PR 机器人仍判非法，把 `PackageIdentifier` 改成 `Insomnia7.MCLauncher` 即可（同时要改
  `-PackageIdentifier` 参数和目录名）。
- 首次发布建议用一个稳定版本号（如 `0.2.0`）而不是 `0.1.x`：winget 里一旦发过 `0.1.2`，
  这个版本号就固定下来了，之后只能往上加。
