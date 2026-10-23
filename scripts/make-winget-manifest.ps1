<#
.SYNOPSIS
    为一个已经打好的便携版 zip 生成 winget 提交清单（4 个 yaml）。

.DESCRIPTION
    winget-pkgs 的目录布局是 manifests/<首字母>/<Publisher>/<Package>/<Version>/，
    本脚本按这个布局把文件直接写到 -OutputDir 下，拷过去就能提 PR。

    清单里的 InstallerSha256 必须与「GitHub Release 上那个 zip」完全一致，
    所以务必用发布产物（或从 Release 下载回来的 zip）来生成，别用本地随手打的包。

.EXAMPLE
    pwsh -NoProfile -File scripts/make-winget-manifest.ps1 `
        -ZipPath dist/MCLauncher-0.1.2-win-x64.zip -Version 0.1.2
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $ZipPath,
    [Parameter(Mandatory = $true)][string] $Version,
    [string] $OutputDir = 'dist/winget',
    [string] $PackageIdentifier = 'Insomnia-7.MCLauncher',
    [string] $Repository = 'Insomnia-7/mc-launcher',
    [string] $Publisher = 'Insomnia-7',
    [string] $PackageName = 'MC Launcher',
    [string] $Locale = 'zh-CN'
)

$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "winget 只接受 主版本.次版本.补丁版本 形式的 PackageVersion，收到：$Version"
}

$zip = Get-Item -LiteralPath ([IO.Path]::GetFullPath($ZipPath)) -ErrorAction Stop
$hash = (Get-FileHash -LiteralPath $zip.FullName -Algorithm SHA256).Hash
$installerUrl = "https://github.com/$Repository/releases/download/v$Version/$($zip.Name)"

$parts = $PackageIdentifier.Split('.')
$publisher = $parts[0]
$package = ($parts[1..($parts.Count - 1)] -join '.')
$dir = Join-Path $OutputDir (Join-Path $publisher.ToLowerInvariant()[0].ToString() `
    (Join-Path $publisher (Join-Path $package $Version)))
New-Item -ItemType Directory -Force -Path $dir | Out-Null

$versionYaml = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.1.6.0.schema.json
PackageIdentifier: $PackageIdentifier
PackageVersion: $Version
DefaultLocale: $Locale
ManifestType: version
ManifestVersion: 1.6.0
"@

$installerYaml = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.1.6.0.schema.json
PackageIdentifier: $PackageIdentifier
PackageVersion: $Version
InstallerType: zip
NestedInstallerType: portable
NestedInstallerFiles:
  - RelativeFilePath: MCLauncher.exe
    PortableCommandAlias: mclauncher
Installers:
  - Architecture: x64
    InstallerUrl: $installerUrl
    InstallerSha256: $hash
    InstallerLocale: $Locale
    Scope: user
ManifestType: installer
ManifestVersion: 1.6.0
"@

$localeYaml = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.locale.1.6.0.schema.json
PackageIdentifier: $PackageIdentifier
PackageVersion: $Version
PackageLocale: $Locale
Publisher: $Publisher
PublisherUrl: https://github.com/$publisher
PublisherSupportUrl: https://github.com/$Repository/issues
PackageName: $PackageName
PackageUrl: https://github.com/$Repository
License: MIT
LicenseUrl: https://github.com/$Repository/blob/main/LICENSE
Copyright: Copyright (c) 2026 MC Launcher contributors
ShortDescription: 轻量、启动快的 Minecraft Java 版启动器
Description: 支持原版与 Fabric / Forge / NeoForge / Quilt，能导入 Modrinth / CurseForge / MultiMC 整合包，内置模组市场、资源包 / 数据包 / 光影下载、正版登录与自动更新。
Moniker: mclauncher
Tags:
  - minecraft
  - launcher
  - modrinth
  - curseforge
  - modpack
ReleaseNotesUrl: https://github.com/$Repository/releases/tag/v$Version
ManifestType: locale
ManifestVersion: 1.6.0
"@

$files = [ordered]@{
    "$PackageIdentifier.yaml"                    = $versionYaml
    "$PackageIdentifier.installer.yaml"          = $installerYaml
    "$PackageIdentifier.locale.$Locale.yaml"     = $localeYaml
}

foreach ($name in $files.Keys) {
    $path = Join-Path $dir $name
    # 统一 LF + UTF-8（无 BOM），winget-pkgs 的 CI 会校验换行与编码
    $text = ($files[$name] -replace "`r`n", "`n").TrimEnd() + "`n"
    [IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))
    Write-Host "已写出 $path"
}

Write-Host ''
Write-Host "InstallerUrl  : $installerUrl"
Write-Host "InstallerSha256: $hash"
Write-Host "提交目录      : $dir"
Write-Host ''
Write-Host '提交方式：把上面这个目录整个拷到 winget-pkgs 仓库的对应路径下，提 PR 即可。'
