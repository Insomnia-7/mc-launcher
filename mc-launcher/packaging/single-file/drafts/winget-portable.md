# 草案：单文件 exe 的 winget 清单（`InstallerType: portable`）

单文件之后清单可以更简单：直接装那个 exe，不再需要 `zip` + `NestedInstallerType` + `NestedInstallerFiles`。
**这只是可选项**，理由与取舍见 `../README.md` §9；`packaging/winget/` 里现有的 zip 清单继续可用。

三个文件对应 `manifests/i/Insomnia-7/MCLauncher/<版本>/`（首字母目录规则见 `packaging/winget/README.md`）。

---

## `Insomnia-7.MCLauncher.yaml`

```yaml
# yaml-language-server: $schema=https://aka.ms/winget-manifest.version.1.6.0.schema.json
PackageIdentifier: Insomnia-7.MCLauncher
PackageVersion: 0.1.2
DefaultLocale: zh-CN
ManifestType: version
ManifestVersion: 1.6.0
```

## `Insomnia-7.MCLauncher.installer.yaml`

```yaml
# yaml-language-server: $schema=https://aka.ms/winget-manifest.installer.1.6.0.schema.json
PackageIdentifier: Insomnia-7.MCLauncher
PackageVersion: 0.1.2
InstallerType: portable
Commands:
  - mclauncher
Installers:
  - Architecture: x64
    InstallerUrl: https://github.com/Insomnia-7/mc-launcher/releases/download/v0.1.2/MCLauncher-0.1.2-win-x64.exe
    InstallerSha256: <64 位小写 hex，与发布资产一致>
    InstallerLocale: zh-CN
    Scope: user
ManifestType: installer
ManifestVersion: 1.6.0
```

## `Insomnia-7.MCLauncher.locale.zh-CN.yaml`

```yaml
# yaml-language-server: $schema=https://aka.ms/winget-manifest.defaultLocale.1.6.0.schema.json
PackageIdentifier: Insomnia-7.MCLauncher
PackageVersion: 0.1.2
PackageLocale: zh-CN
Publisher: Insomnia-7
PublisherUrl: https://github.com/Insomnia-7
PackageName: MC Launcher
PackageUrl: https://github.com/Insomnia-7/mc-launcher
License: <与仓库 LICENSE 一致>
ShortDescription: Minecraft 启动器
Description: Minecraft 启动器，支持实例管理与自动更新。
ManifestType: defaultLocale
ManifestVersion: 1.6.0
```

---

## 注意

1. **`portable` 的卸载只删 exe**。本程序会在安装目录留下 `.backup\`（更新备份）、
   `installed-files.json`（更新账本），并在用户数据目录写实例/配置。`portable` 不会清理这些，
   卸载后会留下残渣——这是继续用 `zip` 清单（`packaging/winget/` 现状）的主要理由。
2. `Commands: [mclauncher]` 生成的 shim 指向那个 exe；exe 被自身更新器替换后 shim 仍然有效（路径不变）。
3. `InstallerSha256` 必须与 Release 资产**逐字节**一致。单文件形态下资产就是那一个 exe，
   更新器替换它不会影响已安装的 winget 记录（版本号仍是清单里的那个，直到发新清单）。
4. 资产名：如果以后把资产改名成 `MCLauncher-<ver>-win-x64.exe`，**`update.json` 不受影响**
   （`UpdateClient.StageAsync` 用 `path` 而不是 url 的文件名，见 `UpdateClient.cs:74`），
   但 winget 清单里的 `InstallerUrl` 要跟着改。
5. 校验：`winget validate --manifest <目录>`；本地没有 winget 时用
   `packaging/winget/validate-manifests.py`（schema + 字段交叉校验）。
