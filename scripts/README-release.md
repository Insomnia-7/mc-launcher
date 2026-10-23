# 发布与自更新

正式发布通过 v* 标签触发，或手工选择一个已存在的版本标签。主程序采用框架依赖的 Windows x64 单 EXE，内嵌独立更新器；目标机需要 .NET 10 Desktop Runtime。

流水线执行顺序：

1. 检出对应标签，构建并运行核心、架构和 WPF 检查。
2. 发布更新器到 payload/updater；配置了 Authenticode 证书时先签更新器。
3. 发布主程序，把已签的更新器资源编入 MCLauncher.exe，再可选签主程序。
4. 校验发布目录仅含主程序，便携 ZIP 放到 dist，避免进入更新文件清单。
5. 生成 update.json，必须且只能包含 MCLauncher.exe。
6. 使用发布环境 Secret 为清单签名，再用公钥独立验签。
7. 上传 MCLauncher.exe、update.json、update.json.sig 和便携 ZIP。

release-signing 环境中的 MCLAUNCHER_UPDATE_SIGNING_KEY 为必需签名私钥。可选的 CODE_SIGNING_PFX_BASE64、CODE_SIGNING_PFX_PASSWORD 用于 Windows 可执行文件签名，只在各自的签名步骤注入。清单签名与 Windows 可执行文件签名是不同用途。

本地构建单文件开发包：

```powershell
pwsh -File scripts/publish-launcher.ps1
```

输出目录默认 mc-launcher/dist/portable，必须为空；可以指定 -OutputDirectory 与 -Version。开发包不会自动发布到 GitHub。

更新清单生成工具仍为 make-update-manifest.ps1，支持 Directory、Version、Notes、BaseUrl、OutFile、PublishedAt 和 FlattenAssetUrls。GitHub Release 地址必须使用 FlattenAssetUrls；签名文件、PDB、日志及备份目录不会进入清单。生成清单后使用 update-signing.ps1 签名，不能再改动清单字节。

更多说明：

- [单文件产物、迁移和验证](../mc-launcher/docs/single-file-release.md)
- [签名密钥与轮换](../mc-launcher/docs/update-signing.md)

## 便携 ZIP 与 winget

流水线将单 EXE 打包到 dist/MCLauncher-<版本>-win-x64.zip。winget 清单应使用实际发布 ZIP 的字节计算哈希，不要拿另行重打的 ZIP 代替。

```powershell
pwsh -File scripts/make-winget-manifest.ps1 -ZipPath dist/MCLauncher-0.1.2-win-x64.zip -Version 0.1.2
```

生成器和官方 schema 验证说明保留在 [winget 交付目录](../mc-launcher/packaging/winget/README.md)。本轮不向 winget 社区提交 PR。
