#Requires -Version 7.0
[CmdletBinding()]
param(
 [Parameter(Mandatory)][ValidateSet('linux-x64','linux-arm64','osx-x64','osx-arm64')][string]$Runtime,
 [Parameter(Mandatory)][string]$PayloadRoot,
 [Parameter(Mandatory)][string]$OutputDirectory,
 [Parameter(Mandatory)][string]$Version,
 [Parameter(Mandatory)][string]$Tag,
 [string]$Notes='', [string]$PrivateKeyPath, [string]$Repository='Insomnia-7/mc-launcher'
)
$ErrorActionPreference='Stop'
if($Tag -ne "desktop-v$Version"){throw 'Version and tag mismatch.'}
$PayloadRoot=[IO.Path]::GetFullPath($PayloadRoot);$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force|Out-Null
$mac=$Runtime.StartsWith('osx-');$entry=if($mac){'MacOS/Fangyu'}else{'Fangyu'}
$paths=if($mac){@($entry,'Info.plist','Resources/fangyu.icns','_CodeSignature/CodeResources')}else{@($entry)}
$files=@();$assets=@()
foreach($path in $paths){
 $source=Get-Item -LiteralPath (Join-Path $PayloadRoot $path)
 $suffix=switch($path){'MacOS/Fangyu'{''} 'Fangyu'{''} 'Info.plist'{'-Info.plist'} 'Resources/fangyu.icns'{'-fangyu.icns'} '_CodeSignature/CodeResources'{'-CodeResources'}}
 $asset="Fangyu-$Runtime$suffix"
 $files+=[ordered]@{path=$path;url="https://github.com/$Repository/releases/download/$Tag/$asset";sha256=(Get-FileHash -LiteralPath $source.FullName -Algorithm SHA256).Hash.ToLowerInvariant();size=$source.Length}
 $assets+=[ordered]@{File=$source.FullName;Name=$asset}
}
$manifest=[ordered]@{version=$Version;runtime=$Runtime;entry=$entry;notes=$Notes;publishedAt=[DateTimeOffset]::UtcNow.ToString('O');files=$files}
$feed=Join-Path $OutputDirectory "update-$Runtime.json"
[IO.File]::WriteAllText($feed,($manifest|ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
& (Join-Path $PSScriptRoot '../../scripts/update-signing.ps1') -ManifestPath $feed -PrivateKeyPath $PrivateKeyPath
if(!$?){throw 'Signing failed.'}
& (Join-Path $PSScriptRoot '../../scripts/update-signing.ps1') -ManifestPath $feed -VerifyOnly
if(!$?){throw 'Signature verification failed.'}
[IO.File]::WriteAllText((Join-Path $OutputDirectory "assets-$Runtime.json"),($assets|ConvertTo-Json -Depth 4))
Write-Output "Signed update: $Runtime"
