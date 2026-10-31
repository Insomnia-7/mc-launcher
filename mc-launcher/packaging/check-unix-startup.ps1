#Requires -Version 7.0
param([Parameter(Mandatory)][string]$Executable,[Parameter(Mandatory)][string]$DataRoot)
$ErrorActionPreference='Stop'
$DataRoot=[IO.Path]::GetFullPath($DataRoot);New-Item -ItemType Directory -Path $DataRoot -Force|Out-Null
$start=[Diagnostics.ProcessStartInfo]::new([IO.Path]::GetFullPath($Executable));$start.UseShellExecute=$false
$start.Environment['MCLAUNCHER_HOME']=Join-Path $DataRoot 'data';$start.Environment['MCLAUNCHER_GAME_ROOT']=Join-Path $DataRoot 'game'
$start.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR']=Join-Path $DataRoot 'bundle';$start.Environment['FANGYU_SMOKE_TEST']='1'
$start.Environment['DOTNET_ROOT']=Join-Path $DataRoot 'no-system-dotnet'
$process=[Diagnostics.Process]::Start($start)
try{
 if(!$process.WaitForExit(60000)){$process.Kill();throw 'Native application startup timed out.'}
 if($process.ExitCode -ne 0){throw "Native application exit code: $($process.ExitCode)"}
 $logs=@(Get-ChildItem -LiteralPath (Join-Path $DataRoot 'data/logs') -Filter 'desktop-*.log')
 if(!$logs.Count -or (Get-Content -LiteralPath $logs[-1].FullName -Raw) -notmatch '界面就绪'){throw 'First ready window was not observed.'}
 [ordered]@{ready=$true;exitCode=$process.ExitCode;executable=[IO.Path]::GetFileName($Executable)}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $DataRoot 'native-startup.json')
 Write-Output 'Native packaged application startup passed.'
}finally{$process.Dispose()}
