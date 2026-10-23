#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $PreviousDirectory,
    [Parameter(Mandatory)][string] $CurrentDirectory,
    [Parameter(Mandatory)][string] $Updater,
    [Parameter(Mandatory)][string] $PrivateKeyPath,
    [Parameter(Mandatory)][string] $WorkDirectory,
    [string] $Version = '0.1.3'
)
$ErrorActionPreference = 'Stop'
$work = [IO.Path]::GetFullPath($WorkDirectory)
if ((Test-Path -LiteralPath $work) -and (Get-ChildItem -LiteralPath $work -Force | Select-Object -First 1)) { throw '测试目录必须为空。' }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dotnet = if ($env:DOTNET_ROOT) { Join-Path $env:DOTNET_ROOT 'dotnet.exe' } else { (Get-Command dotnet).Source }
$cli = Join-Path $repo 'mc-launcher/tools/Launcher.Cli/bin/Release/net10.0/mcl.dll'
$install = Join-Path $work 'install'
$data = Join-Path $work 'data'
New-Item -ItemType Directory -Path $install,$data,(Join-Path $work 'helper') -Force | Out-Null
$previous = [IO.Path]::GetFullPath($PreviousDirectory)
$current = [IO.Path]::GetFullPath($CurrentDirectory)
$previousFiles = @(Get-ChildItem -LiteralPath $previous -Recurse -File)
foreach ($file in $previousFiles) {
    $relative = [IO.Path]::GetRelativePath($previous,$file.FullName)
    if ($relative -in @('update.json','update.json.sig')) { continue }
    $destination = Join-Path $install $relative
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination
}
$oldPaths = @($previousFiles | ForEach-Object { [IO.Path]::GetRelativePath($previous,$_.FullName).Replace('\','/') } | Where-Object { $_ -notin @('update.json','update.json.sig') })
@{version='previous';files=$oldPaths} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $install 'installed-files.json') -Encoding utf8
[IO.File]::WriteAllText((Join-Path $install 'user.keep'),'user fixture')
$exe = Join-Path $install 'MCLauncher.exe'
function Sha([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
function WaitWindow($process) {
    $watch=[Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt 20) {
        if ($process.HasExited) { throw '测试启动器提前退出（请先关闭其他启动器实例）。' }
        $process.Refresh()
        if ($process.MainWindowHandle -ne 0 -and $process.Responding) { return [math]::Round($watch.Elapsed.TotalMilliseconds) }
        Start-Sleep -Milliseconds 20
    }
    throw '测试启动器窗口超时。'
}
$oldHash=Sha $exe
$newHash=Sha (Join-Path $current 'MCLauncher.exe')
if ($oldHash -eq $newHash) { throw '新旧程序必须不同，以验证真实替换。' }
$oldHome=$env:MCLAUNCHER_HOME; $oldGame=$env:MCLAUNCHER_GAME_ROOT; $oldUrl=$env:MCLAUNCHER_UPDATE_URL
$originalProcess=$null; $restartedProcess=$null; $helperProcess=$null
try {
    $env:MCLAUNCHER_HOME=$data; $env:MCLAUNCHER_GAME_ROOT=Join-Path $work 'game'; $env:MCLAUNCHER_UPDATE_URL=''
    pwsh -NoProfile -File (Join-Path $PSScriptRoot 'make-update-manifest.ps1') -Directory $current -Version $Version
    if ($LASTEXITCODE -ne 0) { throw '生成清单失败。' }
    pwsh -NoProfile -File (Join-Path $PSScriptRoot 'update-signing.ps1') -ManifestPath (Join-Path $current 'update.json') -PrivateKeyPath $PrivateKeyPath
    if ($LASTEXITCODE -ne 0) { throw '签名失败。' }
    & $dotnet $cli update stage $current $install
    if ($LASTEXITCODE -ne 0) { throw '暂存失败。' }
    $originalProcess=Start-Process -FilePath $exe -PassThru -WindowStyle Hidden -WorkingDirectory $install
    $oldWindow=WaitWindow $originalProcess
    $helper=Join-Path $work 'helper/mclupd.exe'
    Copy-Item -LiteralPath $Updater -Destination $helper
    $staging=Join-Path $data "update/$Version"
    $arguments='--staging "{0}" --target "{1}" --restart MCLauncher.exe --wait-pid {2}' -f $staging,$install,$originalProcess.Id
    $started=Get-Date
    $helperProcess=Start-Process -FilePath $helper -ArgumentList $arguments -PassThru -WindowStyle Hidden -WorkingDirectory (Split-Path $helper) -RedirectStandardOutput (Join-Path $work 'updater.log') -RedirectStandardError (Join-Path $work 'updater-error.log')
    Start-Sleep -Milliseconds 350
    if ($helperProcess.HasExited -or (Sha $exe) -ne $oldHash) { throw '更新器没有等待旧进程退出。' }
    $originalProcess.CloseMainWindow() | Out-Null
    if (-not $originalProcess.WaitForExit(5000)) { $originalProcess.Kill(); $originalProcess.WaitForExit() }
    if (-not $helperProcess.WaitForExit(60000)) { throw '更新器超时。' }
    if ($helperProcess.ExitCode -ne 0) { throw "更新器失败，查看测试目录日志，退出码 $($helperProcess.ExitCode)" }
    if ((Sha $exe) -ne $newHash) { throw '新版文件哈希不符。' }
    $stale=@($oldPaths | Where-Object { $_ -ne 'MCLauncher.exe' })
    foreach ($relative in $stale) { if (Test-Path -LiteralPath (Join-Path $install $relative)) { throw "旧发布文件未清理：$relative" } }
    if ([IO.File]::ReadAllText((Join-Path $install 'user.keep')) -ne 'user fixture') { throw '用户文件受到影响。' }
    $ledger=Get-Content -LiteralPath (Join-Path $install 'installed-files.json') -Raw | ConvertFrom-Json
    if (@($ledger.files).Count -ne 1 -or $ledger.files[0] -ne 'MCLauncher.exe') { throw '新文件账本不正确。' }
    $backup=Get-ChildItem -LiteralPath (Join-Path $install '.backup') -Directory | Sort-Object Name -Descending | Select-Object -First 1
    foreach ($relative in $oldPaths) {
        if ((Sha (Join-Path $previous $relative)) -ne (Sha (Join-Path $backup.FullName $relative))) { throw "备份内容不正确：$relative" }
    }
    for ($i=0;$i -lt 100;$i++) {
        $restartedProcess=Get-Process MCLauncher -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe -and $_.StartTime -ge $started } | Select-Object -First 1
        if ($restartedProcess) { break }; Start-Sleep -Milliseconds 50
    }
    if (-not $restartedProcess) { throw '未发现自动重启的测试程序。' }
    $newWindow=WaitWindow $restartedProcess
    [ordered]@{ oldFiles=$oldPaths.Count;removed=$stale.Count;version=$Version;waitPid=$true;hashMatched=$true;backupMatched=$true;userFilePreserved=$true;restartWindow=$true;oldWindowMs=$oldWindow;newWindowMs=$newWindow } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $work 'result.json') -Encoding utf8
    Write-Host "PASS：$($oldPaths.Count) 文件 → 单 EXE；清理 $($stale.Count) 个旧发布文件；备份、用户文件、等待退出和自动重启均通过。"
} finally {
    # 只关闭本测试目录里自己启动的进程，不按进程名批量关闭用户程序。
    foreach ($process in @($originalProcess,$restartedProcess,$helperProcess)) {
        if ($null -ne $process -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
    }
    $env:MCLAUNCHER_HOME=$oldHome; $env:MCLAUNCHER_GAME_ROOT=$oldGame; $env:MCLAUNCHER_UPDATE_URL=$oldUrl
}
