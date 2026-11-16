#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('M0','M1','M2','M3','M4','M5','M6','All')][string]$Stage='All',
    [string]$OutputDirectory='',
    [switch]$SkipBuild
)
$ErrorActionPreference='Stop'
$appRoot=Split-Path -Parent $PSScriptRoot
if(!$OutputDirectory){$OutputDirectory=Join-Path $appRoot ('scratch/ui-a-'+(Get-Date -Format yyyyMMdd-HHmmss))}
New-Item -ItemType Directory -Path $OutputDirectory -Force|Out-Null
$OutputDirectory=(Resolve-Path -LiteralPath $OutputDirectory).Path
$env:TEMP='D:\.codex\temp';$env:TMP=$env:TEMP
if(!$env:DOTNET_CLI_HOME){$env:DOTNET_CLI_HOME='D:\.codex\tool-cache\dotnet-cli'}
if(!$env:NUGET_PACKAGES){$env:NUGET_PACKAGES='D:\nuget\packages'}
$env:NUGET_HTTP_CACHE_PATH='D:\.codex\tool-cache\nuget-http'
$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR='D:\.codex\temp\fangyu-bundle'
if(!$SkipBuild){
    & dotnet build (Join-Path $appRoot 'MinecraftLauncher.Desktop.slnx') -c Release -m:1 -nr:false -p:UseSharedCompilation=false --nologo *> (Join-Path $OutputDirectory 'build.log')
    if($LASTEXITCODE -ne 0){throw "Build failed: $OutputDirectory/build.log"}
}
$modes=[ordered]@{M1=@('workbench-shell');M2=@('workbench');M3=@('workbench-manage');M4=@('workbench-catalog','catalog-refinement','resource-interaction','multiplayer');M5=@('workbench-account','workbench-consistency');M6=@('workbench-native','resource-native','startup-native')}
if($Stage -in @('M0','All')){
    foreach($project in @('Architecture.Check','Core.Checks')){
        & dotnet (Join-Path $appRoot "tests/$project/bin/Release/net10.0/$project.dll") *> (Join-Path $OutputDirectory "$project.log")
        if($LASTEXITCODE -ne 0){throw "$project failed"}
    }
    Write-Output 'M0: architecture and core checks passed.'
}
foreach($entry in $modes.GetEnumerator()){
    if($Stage -notin @('All',$entry.Key)){continue}
    foreach($mode in $entry.Value){
        if($mode.EndsWith('-native') -and !$IsWindows){Write-Output 'M6 native Windows check pending: Windows environment required.';continue}
        $case=Join-Path $OutputDirectory $mode
        & dotnet (Join-Path $appRoot 'tests/Desktop.Checks/bin/Release/net10.0/Desktop.Checks.dll') $case "--$mode" *> (Join-Path $OutputDirectory "$mode.log")
        if($LASTEXITCODE -ne 0){throw "$($entry.Key) $mode failed: $OutputDirectory/$mode.log"}
        Write-Output "$($entry.Key): $mode passed."
    }
}
Write-Output "Evidence: $OutputDirectory"
