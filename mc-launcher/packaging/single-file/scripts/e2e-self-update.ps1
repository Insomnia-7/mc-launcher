<#
.SYNOPSIS
    端到端验证「单文件 exe 能不能自更新」。

.DESCRIPTION
    复现真实链路：已安装的 MCLauncher.exe（单文件）正在运行 → 起更新器并 --wait-pid 等它退出
    → 主程序退出 → 更新器替换 exe → 更新器重启新版本。

    两次跑：
      阶段 1  独立的 mclupd.exe（等价于现状多文件布局里 updater\mclupd.exe 的用法）
      阶段 2  用 probe-embed.exe 模拟「更新器内嵌在主程序里、更新时才解到 %TEMP%」的做法

    验证点：更新器退出码 0 / 替换后 sha256 等于新版 / 文件版本号 / 备份里是旧版 /
            重启后的进程能起窗口 / updater.log 记了「备份」「替换」「账本已更新」。

.PARAMETER OldExe
    旧版本产物（0.1.2 的 MCLauncher.exe，单文件）。

.PARAMETER NewExe
    新版本产物（0.1.3 的 MCLauncher.exe，单文件）。

.PARAMETER Updater
    更新器 mclupd.exe（必须与 OldExe 的运行时形态匹配：FD 主程序配 FD 更新器，SC 配 SC）。

.PARAMETER ProbeEmbed
    可选。probe-embed.exe 的路径；不给就跳过阶段 2。

.EXAMPLE
    .\e2e-self-update.ps1 -OldExe .\out\sc\MCLauncher.exe -NewExe .\out\sc-new\MCLauncher.exe -Updater .\out\_updater-sc-compressed\mclupd.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OldExe,
    [Parameter(Mandatory = $true)][string]$NewExe,
    [Parameter(Mandatory = $true)][string]$Updater,
    [string]$ProbeEmbed,
    [string]$NewVersion = '0.1.3',
    [string]$WorkDir = (Join-Path $env:TEMP 'mc-single-file\e2e'),
    [string]$KeysDir = (Join-Path $env:TEMP 'mc-single-file\keys')
)

$ErrorActionPreference = 'Stop'
$signer = Join-Path $PSScriptRoot 'sign-manifest.ps1'

$install = Join-Path $WorkDir 'install'
$staging = Join-Path $WorkDir "staging\$NewVersion"
$appPath = Join-Path $install 'MCLauncher.exe'

function Sha([string]$p) { (Get-FileHash -Algorithm SHA256 -Path $p).Hash.ToLowerInvariant() }
function Wait-Window($proc, [int]$timeoutSec = 60) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $timeoutSec) {
        Start-Sleep -Milliseconds 20
        if ($proc.HasExited) { return $false }
        $proc.Refresh()
        if ($proc.MainWindowHandle -ne 0) { return $true }
    }
    return $false
}
function Kill-Apps {
    Get-Process MCLauncher -ErrorAction SilentlyContinue | ForEach-Object {
        try { $_.Kill(); $_.WaitForExit(10000) | Out-Null } catch { }
    }
}
function Write-Manifest {
    param([string]$Hash, [long]$Size)
    $json = [ordered]@{
        version     = $NewVersion
        entry       = 'MCLauncher.exe'
        notes       = 'single-file e2e test'
        publishedAt = (Get-Date).ToString('o')
        files       = @([ordered]@{ path = 'MCLauncher.exe'; url = 'https://example.invalid/MCLauncher.exe'; sha256 = $Hash; size = $Size })
    } | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText((Join-Path $staging 'update.json'), $json, [Text.UTF8Encoding]::new($false))
    & $signer -Sign -ManifestPath (Join-Path $staging 'update.json') -KeysDir $KeysDir | Out-Null
    Copy-Item $NewExe (Join-Path $staging 'MCLauncher.exe') -Force
}

Kill-Apps
if (Test-Path $WorkDir) { Remove-Item -Recurse -Force $WorkDir }
New-Item -ItemType Directory -Force -Path $install | Out-Null
New-Item -ItemType Directory -Force -Path $staging | Out-Null
Copy-Item $OldExe $appPath
Copy-Item $Updater (Join-Path $WorkDir 'mclupd.exe') -Force

$oldHash = Sha $appPath
$newHash = Sha $NewExe
$newSize = (Get-Item $NewExe).Length
Write-Host "旧版 exe : $oldHash  $([math]::Round((Get-Item $appPath).Length/1MB,2)) MB"
Write-Host "新版 exe : $newHash  $([math]::Round($newSize/1MB,2)) MB"
Write-Manifest -Hash $newHash -Size $newSize

# ---------- 阶段 1：独立 mclupd.exe ----------
Write-Host ''
Write-Host '########## 阶段 1：独立 mclupd.exe + --wait-pid ##########'
$app = Start-Process -FilePath $appPath -PassThru
if (-not (Wait-Window $app 60)) { throw '旧版没能起窗口' }
Write-Host "  已安装版本运行中 pid=$($app.Id)"

$upd = Start-Process -FilePath (Join-Path $WorkDir 'mclupd.exe') -PassThru -NoNewWindow `
    -ArgumentList @('--staging', $staging, '--target', $install, '--restart', 'MCLauncher.exe', '--wait-pid', $app.Id) `
    -RedirectStandardOutput (Join-Path $WorkDir 'updater.out.log') `
    -RedirectStandardError (Join-Path $WorkDir 'updater.err.log')
Start-Sleep -Seconds 3
Write-Host "  更新器 pid=$($upd.Id) 仍在等待 = $(-not $upd.HasExited)（主程序还活着 = $(-not $app.HasExited)）"
Write-Host "  等待期间 exe 未被替换 = $((Sha $appPath) -eq $oldHash)"

$app.Kill(); $app.WaitForExit(15000) | Out-Null
if (-not $upd.WaitForExit(180000)) { $upd.Kill(); throw '更新器超时' }
Write-Host "  更新器退出码 = $($upd.ExitCode)"
Get-Content (Join-Path $WorkDir 'updater.out.log') | ForEach-Object { "    [out] $_" }
$err = Get-Content (Join-Path $WorkDir 'updater.err.log') -ErrorAction SilentlyContinue
if ($err) { $err | ForEach-Object { "    [err] $_" } }

Write-Host "  替换后匹配新版 = $((Sha $appPath) -eq $newHash)；文件版本 = $((Get-Item $appPath).VersionInfo.ProductVersion)"
$backup = Get-ChildItem (Join-Path $install '.backup') -Directory -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending | Select-Object -First 1
if ($backup) {
    Write-Host "  备份目录 = $($backup.Name)；备份里是旧版 = $((Sha (Join-Path $backup.FullName 'MCLauncher.exe')) -eq $oldHash)"
    $log = Join-Path $backup.FullName 'updater.log'
    if (Test-Path $log) { Get-Content $log | ForEach-Object { "    [log] $_" } }
}
Start-Sleep -Seconds 3
$rs = Get-Process MCLauncher -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $appPath }
Write-Host "  重启后的进程 = $($rs.Id -join ','); 起窗口 = $(if ($rs) { Wait-Window $rs 30 } else { $false })"
Kill-Apps

# ---------- 阶段 2：内嵌更新器（解到 %TEMP%） ----------
if (-not $ProbeEmbed) {
    Write-Host ''
    Write-Host '（没给 -ProbeEmbed，跳过阶段 2）'
    return
}

Write-Host ''
Write-Host '########## 阶段 2：更新器内嵌、更新时解到 %TEMP%（probe-embed） ##########'
Copy-Item $OldExe $appPath -Force
Write-Manifest -Hash $newHash -Size $newSize
Write-Host "  已还原成旧版 = $((Sha $appPath) -eq $oldHash)"

$app2 = Start-Process -FilePath $appPath -PassThru
if (-not (Wait-Window $app2 60)) { throw '旧版没能起窗口' }
Write-Host "  已安装版本运行中 pid=$($app2.Id)"

$upd2 = Start-Process -FilePath $ProbeEmbed -PassThru -NoNewWindow `
    -ArgumentList @('--staging', $staging, '--target', $install, '--restart', 'MCLauncher.exe', '--wait-pid', $app2.Id) `
    -RedirectStandardOutput (Join-Path $WorkDir 'probe.out.log') `
    -RedirectStandardError (Join-Path $WorkDir 'probe.err.log')
Start-Sleep -Seconds 3
Write-Host "  probe pid=$($upd2.Id) 仍在等待 = $(-not $upd2.HasExited)"
$app2.Kill(); $app2.WaitForExit(15000) | Out-Null
if (-not $upd2.WaitForExit(180000)) { $upd2.Kill(); throw 'probe 超时' }
Write-Host "  probe 退出码 = $($upd2.ExitCode)"
Get-Content (Join-Path $WorkDir 'probe.out.log') | ForEach-Object { "    [out] $_" }
$e2 = Get-Content (Join-Path $WorkDir 'probe.err.log') -ErrorAction SilentlyContinue
if ($e2) { $e2 | ForEach-Object { "    [err] $_" } }
Write-Host "  替换后匹配新版 = $((Sha $appPath) -eq $newHash)；文件版本 = $((Get-Item $appPath).VersionInfo.ProductVersion)"
Start-Sleep -Seconds 3
$rs2 = Get-Process MCLauncher -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $appPath }
Write-Host "  重启后的进程 = $($rs2.Id -join ','); 起窗口 = $(if ($rs2) { Wait-Window $rs2 30 } else { $false })"
Kill-Apps
