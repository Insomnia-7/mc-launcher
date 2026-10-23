# 草案：`release.yml` 改成单文件发布

**本次没有改 `.github/workflows/release.yml`**（任务边界只允许新增 `packaging/single-file/**`）。
下面是逐条可套用的改动，对应仓库当前版本（`release.yml` 共 299 行）。

改动会让流水线产出**一个 2.39 MB 的 `MCLauncher.exe`**（框架依赖单文件 + 内嵌更新器，见 `../README.md` §5）。

---

## 0. 必须同时改的四处（按执行顺序）

| # | 位置（当前行） | 改什么 | 不改的后果 |
|---|---|---|---|
| 1 | `发布更新器` (107–120) | 产物落到 `payload/updater/`，**不要落 `out/`** | 落进 `out/` 会被清单扫成一个要替换的文件，客户端每次更新多下一个 0.6 MB 更新器并试图覆盖自己 |
| 2 | `发布主程序（WPF）` (91–101) | 加 `-r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:UpdaterPayload=payload/updater/mclupd.exe` | 单文件起不来（WPF 原生依赖没解出来）／更新器没被编进去 |
| 3 | `校验 update.json` (267–269) | 硬性要求 `updater/mclupd.exe` → 改成要求主程序 | 单文件清单里没有 `updater/mclupd.exe`，这一步必然 `throw` |
| 4 | 步骤顺序 | `发布更新器` 必须排在 `发布主程序` **之前** | `-p:UpdaterPayload=` 指向的文件还不存在，`EmbeddedResource` 的 `Exists()` 条件不成立，静默不内嵌 → 更新按钮失效 |

`代码签名` (166) 那行 `@('out/MCLauncher.exe','out/updater/mclupd.exe') | Where-Object { Test-Path $_ }`
**不用改**：单文件下第二条不存在，会自动只签主程序。

---

## 1. `发布更新器`：改输出目录 + 排到主程序之前

```diff
-      - name: 发布更新器
+      # 更新器产物必须落在 out/ 之外：out/ 是更新清单的扫描目录，
+      # 落进去会让客户端把「更新器自己」也当成一个要替换的文件。
+      - name: 发布更新器
         shell: pwsh
         run: |
           dotnet publish mc-launcher/tools/Launcher.Updater `
             -c Release `
-            -o out/updater `
+            -o payload/updater `
             -r win-x64 `
             --self-contained false `
             -p:PublishSingleFile=true `
             -p:Version=${{ steps.version.outputs.version }} `
             -p:ContinuousIntegrationBuild=true `
             -p:DebugType=None `
             -p:DebugSymbols=false
           if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
```

然后把这个步骤整块移到 `发布主程序（WPF）` 之前。

---

## 2. `发布主程序（WPF）`：加单文件参数 + 内嵌更新器

```diff
-      # 主程序：MCLauncher.exe + dll 等，直接落到 out/
+      # 主程序：单文件，直接落到 out/（整个应用就是 out/MCLauncher.exe 一个文件）
+      # -p:UpdaterPayload 把上一步的 mclupd.exe 编进去；单文件布局没有 updater\ 目录，
+      # 运行时由 MainViewModel.ExtractEmbeddedUpdater 解到 %TEMP% 再跑。
       # -p:Version 让程序里的版本号和 tag 一致，不依赖 csproj 里写死的 0.1.0
       # 不产 pdb：清单默认排除调试符号，也不该把它们当 Release 资产传上去
       - name: 发布主程序（WPF）
         shell: pwsh
         run: |
           dotnet publish mc-launcher/src/Launcher.App `
             -c Release `
+            -r win-x64 `
+            --self-contained false `
             -o out `
+            -p:PublishSingleFile=true `
+            -p:IncludeNativeLibrariesForSelfExtract=true `
+            -p:UpdaterPayload=payload/updater/mclupd.exe `
             -p:Version=${{ steps.version.outputs.version }} `
             -p:ContinuousIntegrationBuild=true `
             -p:DebugType=None `
             -p:DebugSymbols=false
           if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
```

`--self-contained false` 是显式的：不写也会默认 false，但配上 `-r win-x64` 之后建议写明，避免以后有人加
`-p:SelfContained=true` 时行为漂移。

---

## 3. 新增：单文件产物守卫（建议放在 `检查资产名唯一` 之前）

```yaml
      - name: 校验单文件产物
        shell: pwsh
        run: |
          $ErrorActionPreference = 'Stop'
          if (-not (Test-Path out/MCLauncher.exe)) { throw '缺少 out/MCLauncher.exe' }
          $extra = Get-ChildItem out -Recurse -File |
            Where-Object { $_.Name -ne 'MCLauncher.exe' -and $_.Name -ne 'update.json' }
          if ($extra) {
            throw ("单文件发布却多出文件：" + (($extra | ForEach-Object { $_.FullName }) -join ', '))
          }
          Write-Host ("单文件产物 OK：{0:N2} MB" -f ((Get-Item out/MCLauncher.exe).Length / 1MB))
```

这条守卫挡的是「`PublishSingleFile` 没生效 / pdb 没关掉 / `UpdaterPayload` 误落到 out/」这三类事故。
实测：漏掉 `-p:DebugType=none` 时 `out/` 里会多出 4 个 `.pdb`，正好被它拦下。

---

## 4. `校验 update.json`：别再硬要求 `updater/mclupd.exe`

```diff
-          if (-not ($files | Where-Object { $_.path -eq 'updater/mclupd.exe' })) {
-            throw 'update.json 里没有 updater/mclupd.exe，自更新会缺更新器'
-          }
+          # 单文件发布：清单里只有主程序；多文件布局（老版本）才会有 updater/mclupd.exe。
+          if (-not ($files | Where-Object { $_.path -eq 'MCLauncher.exe' -or $_.path -eq 'updater/mclupd.exe' })) {
+            throw 'update.json 里既没有主程序也没有更新器，自更新会缺文件'
+          }
```

保留 `updater/mclupd.exe` 的兼容分支，是为了「万一要回退到多文件发布」时这条校验不至于把人挡住。

---

## 5. 签名：更新器要在**内嵌之前**签

现状是「两条 publish 都跑完 → 签名 → 打 zip」，签的是 `out/updater/mclupd.exe`。
单文件之后那个文件在 `payload/updater/`，而且它会被**复制进主程序**——所以：

- 想让用户在 `%TEMP%` 里跑到的更新器也是**已签名**的，必须在 `发布主程序` **之前**签 `payload/updater/mclupd.exe`；
- 签完再内嵌，主程序 exe 再单独签一次（外层的 Authenticode 覆盖整个文件，包括内嵌的更新器字节）。

推荐顺序：

```
发布更新器 → 检测签名凭据 → 签 payload/updater/mclupd.exe → 发布主程序（内嵌）→ 签 out/MCLauncher.exe → …
```

```diff
-            $targets = @('out/MCLauncher.exe', 'out/updater/mclupd.exe') |
-              Where-Object { Test-Path $_ }
+            # 单文件布局：先签 payload 里的更新器（它随后会被内嵌进主程序），再签主程序。
+            # 多文件布局（回退时）：签 out/updater/mclupd.exe。
+            $targets = @('payload/updater/mclupd.exe', 'out/MCLauncher.exe', 'out/updater/mclupd.exe') |
+              Where-Object { Test-Path $_ } |
+              Select-Object -Unique
```

并把签名步骤拆成两段：第一段在 `发布主程序` 前签 payload，第二段在主程序发布后签 exe。
不拆也能跑（更新器在 `%TEMP%` 里不带签名而已），但 Windows 首次执行未签名 exe 会弹拦截，
所以建议拆。

---

## 6. 不需要改的部分（确认过）

- **`打包便携版 zip`**（206–221）：`Compress-Archive -Path out/*` 现在压的是「一个 exe」。
  实测（`fd-embedded` 形态，2,508,364 B 的 exe）：zip **1,072,276 B（1.02 MB），比例 42.7%**，
  远高于 `0.25 * $source` 的守卫线（627,091 B），不会误触发。
  **注意**：这版守卫已经是「不低于源体积 25%」而不是写死的 `1MB`（`release.yml:217-221` 的注释说明了原因），
  所以「0.91 MB 的 zip 被 `1MB` 下限打断」那个问题在当前版本里已经不存在了——
  如果 `task-11` 的报告里还写着这条，以现在这份文件为准。
- **`生成 update.json`**（225–244）：`-FlattenAssetUrls` 保留。单文件清单只有 1 条：
  `path = "MCLauncher.exe"`、`url = ".../v0.1.3/MCLauncher.exe"`、`sha256`、`size`。
  客户端 `UpdateClient.StageAsync` 用 `path` 决定暂存文件名（`UpdateClient.cs:74`），**不看 url 的文件名**，
  所以以后想把资产改名成 `MCLauncher-<ver>-win-x64.exe` 也不会破坏更新。
- **`发布到 GitHub Release`**（288–299）：`out/**` + `dist/*.zip` 不变。
- **`检查资产名唯一`**（185–200）：单文件下只剩一个名字，天然通过；留着当回归守卫。

---

## 7. 回退方式

改回多文件布局只需：去掉 `发布主程序` 里的 `-r win-x64`、`--self-contained false`、
`-p:PublishSingleFile=true`、`-p:IncludeNativeLibrariesForSelfExtract=true`、`-p:UpdaterPayload=`，
并把 `发布更新器` 的 `-o` 改回 `out/updater`。源码侧的补丁**不用回退**：`PublishSingleFile` 条件在
多文件发布下不生效，`CopyUpdater` 与 `ProjectReference` 行为与现在完全一致（`drafts/src-patch.md` §4 第 1 步验证的就是这点）。
