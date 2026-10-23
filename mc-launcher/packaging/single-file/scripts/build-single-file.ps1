<#
.SYNOPSIS
    构建 MCLauncher 的各种「单文件」形态，并把体积列成表。

.DESCRIPTION
    等价于 release.yml 里那两条 dotnet publish，只是把发布参数暴露出来做对比。
    产物落在 <WorkDir>\<flavor>\，每个 flavor 一个目录。

    注意：源码必须是隔离副本。仓库里正在运行的 MCLauncher.exe 会锁住
    bin\...\net10.0-windows\*.dll，就地发布会被 MSB3027 打断。

.PARAMETER Flavor
    all                所有形态（默认）
    fd                 框架依赖单文件（不含更新器）
    fd-embedded        框架依赖单文件 + 内嵌更新器  ← 推荐方案
    sc                 自包含单文件
    sc-compressed      自包含单文件 + 压缩
    sc-embedded        自包含单文件 + 压缩 + 内嵌更新器

.EXAMPLE
    .\build-single-file.ps1 -Flavor fd-embedded -Version 0.1.2
#>
[CmdletBinding()]
param(
    [ValidateSet('all', 'fd', 'fd-embedded', 'sc', 'sc-compressed', 'sc-embedded')]
    [string]$Flavor = 'all',
    [string]$WorkDir = (Join-Path $env:TEMP 'mc-single-file'),
    [string]$Version = '0.1.2',
    [string]$Dotnet = 'dotnet'
)

$ErrorActionPreference = 'Stop'

$srcRoot = Join-Path $WorkDir 'src'
$outRoot = Join-Path $WorkDir 'out'
$payload = Join-Path $WorkDir 'payload'
$appProj = Join-Path $srcRoot 'src\Launcher.App\Launcher.App.csproj'
$updProj = Join-Path $srcRoot 'tools\Launcher.Updater\Launcher.Updater.csproj'

foreach ($p in @($appProj, $updProj)) {
    if (-not (Test-Path $p)) {
        throw "找不到工程：$p（先用 robocopy 把仓库里的 mc-launcher 复制到 $srcRoot）"
    }
}

New-Item -ItemType Directory -Force -Path $outRoot | Out-Null
New-Item -ItemType Directory -Force -Path $payload | Out-Null

$nopdb = @('-p:DebugType=none', '-p:DebugSymbols=false')
$rows = @()

function Publish-Project {
    param([string]$Name, [string]$Project, [string[]]$Extra, [string]$Dest)
    if (Test-Path $Dest) { Remove-Item -Recurse -Force $Dest }
    Write-Host "=== $Name"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    & $Dotnet publish $Project -c Release "-p:Version=$Version" -o $Dest @Extra 2>&1 |
        Tee-Object -FilePath (Join-Path $outRoot "$Name.build.log")
    $code = $LASTEXITCODE
    $sw.Stop()
    if ($code -ne 0) {
        Write-Host "  失败（$([math]::Round($sw.Elapsed.TotalSeconds,1))s），日志：$outRoot\$Name.build.log"
        $script:rows += [pscustomobject]@{ 形态 = $Name; 成功 = $false; 文件数 = 0; 总体积MB = 0; exe体积MB = 0; 秒 = [math]::Round($sw.Elapsed.TotalSeconds, 1) }
        return
    }
    $files = Get-ChildItem $Dest -Recurse -File
    $exe = $files | Where-Object { $_.Extension -eq '.exe' } | Select-Object -First 1
    $script:rows += [pscustomobject]@{
        形态     = $Name
        成功     = $true
        文件数   = $files.Count
        总体积MB = [math]::Round((($files | Measure-Object Length -Sum).Sum / 1MB), 2)
        exe体积MB = if ($exe) { [math]::Round($exe.Length / 1MB, 2) } else { 0 }
        秒       = [math]::Round($sw.Elapsed.TotalSeconds, 1)
    }
}

# 更新器：框架依赖单文件（给 fd 形态内嵌）与自包含压缩单文件（给 sc 形态内嵌）
# 产物放 payload\ 而不是 out\：out\ 是更新清单的扫描目录
$updFd = Join-Path $payload 'updater-fd\mclupd.exe'
$updSc = Join-Path $payload 'updater-sc-compressed\mclupd.exe'

if ($Flavor -in @('all', 'fd', 'fd-embedded')) {
    Publish-Project -Name 'updater-fd' -Project $updProj -Dest (Join-Path $payload 'updater-fd') `
        -Extra (@('-r', 'win-x64', '--self-contained', 'false', '-p:PublishSingleFile=true') + $nopdb)
}
if ($Flavor -in @('all', 'sc', 'sc-compressed', 'sc-embedded')) {
    Publish-Project -Name 'updater-sc-compressed' -Project $updProj -Dest (Join-Path $payload 'updater-sc-compressed') `
        -Extra (@('-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:EnableCompressionInSingleFile=true') + $nopdb)
}

$appBase = @('-r', 'win-x64', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true') + $nopdb

if ($Flavor -in @('all', 'fd')) {
    Publish-Project -Name 'fd' -Project $appProj -Dest (Join-Path $outRoot 'fd') `
        -Extra ($appBase + @('--self-contained', 'false'))
}
if ($Flavor -in @('all', 'fd-embedded')) {
    if (-not (Test-Path $updFd)) { throw "缺 $updFd（先跑 -Flavor fd 生成更新器）" }
    Publish-Project -Name 'fd-embedded' -Project $appProj -Dest (Join-Path $outRoot 'fd-embedded') `
        -Extra ($appBase + @('--self-contained', 'false', "-p:UpdaterPayload=$updFd"))
}
if ($Flavor -in @('all', 'sc')) {
    Publish-Project -Name 'sc' -Project $appProj -Dest (Join-Path $outRoot 'sc') `
        -Extra ($appBase + @('--self-contained', 'true'))
}
if ($Flavor -in @('all', 'sc-compressed')) {
    Publish-Project -Name 'sc-compressed' -Project $appProj -Dest (Join-Path $outRoot 'sc-compressed') `
        -Extra ($appBase + @('--self-contained', 'true', '-p:EnableCompressionInSingleFile=true'))
}
if ($Flavor -in @('all', 'sc-embedded')) {
    if (-not (Test-Path $updSc)) { throw "缺 $updSc（先跑 -Flavor sc-compressed 生成更新器）" }
    Publish-Project -Name 'sc-embedded' -Project $appProj -Dest (Join-Path $outRoot 'sc-embedded') `
        -Extra ($appBase + @('--self-contained', 'true', '-p:EnableCompressionInSingleFile=true', "-p:UpdaterPayload=$updSc"))
}

$rows | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
$rows | ConvertTo-Json | Set-Content (Join-Path $outRoot 'build-matrix.json') -Encoding utf8
Write-Host "产物：$outRoot"
