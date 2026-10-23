<#
.SYNOPSIS
    测量 MCLauncher 的首帧时间（窗口句柄出现）。

.DESCRIPTION
    每次 Start-Process 起进程，轮询 MainWindowHandle != 0 记为「首帧」，然后杀掉进程。
    这是「窗口出现」而不是「可交互」，数值偏乐观，但同口径下不同形态可横向比较。

.PARAMETER Exe
    一个或多个 MCLauncher.exe 路径。

.PARAMETER Cold
    每次测量前删掉 .NET 单文件解压缓存（%TEMP%\.net\MCLauncher），模拟首次启动。

.EXAMPLE
    .\measure-launch.ps1 -Exe .\out\fd-embedded\MCLauncher.exe -Runs 5
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string[]]$Exe,
    [int]$Runs = 5,
    [switch]$Cold,
    [int]$TimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'

function Clear-ExtractCache {
    $cache = Join-Path $env:TEMP '.net\MCLauncher'
    if (Test-Path $cache) { Remove-Item -Recurse -Force $cache -ErrorAction SilentlyContinue }
}

function Measure-One {
    param([string]$Path)
    if (-not (Test-Path $Path)) { Write-Host "跳过（不存在）：$Path"; return }
    Get-Process MCLauncher -ErrorAction SilentlyContinue | ForEach-Object {
        try { $_.Kill(); $_.WaitForExit(5000) | Out-Null } catch { }
    }
    Start-Sleep -Milliseconds 300

    $times = @()
    for ($i = 1; $i -le $Runs; $i++) {
        if ($Cold) { Clear-ExtractCache }
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $p = Start-Process -FilePath $Path -PassThru
        $ok = $false
        while ($sw.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
            Start-Sleep -Milliseconds 10
            if ($p.HasExited) { break }
            $p.Refresh()
            if ($p.MainWindowHandle -ne 0) { $ok = $true; break }
        }
        $sw.Stop()
        $ms = [int]$sw.Elapsed.TotalMilliseconds
        Write-Host ("  run {0}: {1} ms {2}" -f $i, $ms, $(if ($ok) { '' } else { '[NO WINDOW]' }))
        $times += $ms
        try { $p.Kill(); $p.WaitForExit(5000) | Out-Null } catch { }
        Start-Sleep -Milliseconds 250
    }

    $sorted = @($times | Sort-Object)
    $mid = [int][math]::Floor($sorted.Count / 2)
    Write-Host ("  {0}" -f (Split-Path $Path -Parent))
    Write-Host ("    中位数 {0} ms / 最小 {1} / 最大 {2} / 均值 {3}" -f `
            $sorted[$mid], $sorted[0], $sorted[-1], [int](($times | Measure-Object -Average).Average))
    Write-Host ''
}

foreach ($e in $Exe) { Measure-One -Path $e }

Get-Process MCLauncher -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }
