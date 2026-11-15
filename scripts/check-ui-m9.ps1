param(
    [string]$Dotnet = 'dotnet',
    [string]$OutputDirectory,
    [switch]$Native,
    [switch]$RealGame
)
$ErrorActionPreference='Stop'
$sourceRoot=Split-Path -Parent $PSScriptRoot
$projectRoot=Join-Path $sourceRoot 'mc-launcher'
if(!$OutputDirectory){$OutputDirectory=Join-Path $projectRoot 'scratch/ui-m9'}
if($IsWindows){
    $env:TEMP='D:\.codex\temp';$env:TMP=$env:TEMP
    $env:DOTNET_CLI_HOME='D:\.codex\tool-cache\dotnet-cli'
    $env:NUGET_PACKAGES='D:\nuget\packages'
    $env:NUGET_HTTP_CACHE_PATH='D:\.codex\tool-cache\nuget-http'
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR='D:\.codex\temp\fangyu-bundle'
}
New-Item -ItemType Directory -Path $OutputDirectory -Force|Out-Null
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
function Invoke-Check([string]$Name,[string[]]$Arguments){
    $log=Join-Path $OutputDirectory ($Name+'.log')
    & $Dotnet @Arguments > $log 2>&1
    if($LASTEXITCODE -ne 0){Get-Content -LiteralPath $log -Tail 20;throw "$Name failed. See $log"}
    Write-Output "$Name passed"
}
Push-Location -LiteralPath $projectRoot
try{
    Invoke-Check 'restore' @('restore','MinecraftLauncher.Desktop.slnx')
    Invoke-Check 'build-desktop' @('build','MinecraftLauncher.Desktop.slnx','-c','Release','--no-restore')
    if($IsWindows){
        Invoke-Check 'build-wpf' @('build','MinecraftLauncher.sln','-c','Release')
        Invoke-Check 'wpf' @('tests/App.Checks/bin/Release/net10.0-windows/App.Checks.dll')
    }
    Invoke-Check 'architecture' @('tests/Architecture.Check/bin/Release/net10.0/Architecture.Check.dll')
    Invoke-Check 'core' @('tests/Core.Checks/bin/Release/net10.0/Core.Checks.dll')
    $modes=@('full','empty','legacy-theme','unknown-theme','workspace','multiplayer','redesign','shell','sidebar','page-motion','home','components','showcase','showcase-disabled','showcase-invalid')
    if($Native){$modes+=@('redesign-native','shell-native','sidebar-native','page-motion-native','home-native','components-native','showcase-native','gpu-native')}
    if($RealGame){$modes+='real-game'}
    foreach($mode in $modes){
        $case=Join-Path $OutputDirectory ($mode+'-'+[Guid]::NewGuid().ToString('N'))
        $arguments=@('tests/Desktop.Checks/bin/Release/net10.0/Desktop.Checks.dll',$case)
        if($mode -ne 'full'){$arguments+='--'+$mode}
        Invoke-Check $mode $arguments
    }
}finally{Pop-Location}
