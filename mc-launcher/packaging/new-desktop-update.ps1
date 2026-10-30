#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Tag,
    [string]$Notes = '',
    [string]$PrivateKeyPath,
    [string]$Repository = 'Insomnia-7/mc-launcher'
)
$ErrorActionPreference='Stop'
$file=Get-Item -LiteralPath $Executable
if($Tag -ne "desktop-v$Version" -or !$file.VersionInfo.ProductVersion.StartsWith($Version+'+')){throw '版本、标签和 EXE 的版本信息不一致。'}
$directory=$file.DirectoryName
$manifest=[ordered]@{
 version=$Version
 entry='Fangyu.exe'
 notes=$Notes
 publishedAt=[DateTimeOffset]::UtcNow.ToString('O')
 files=@([ordered]@{path='Fangyu.exe';url="https://github.com/$Repository/releases/download/$Tag/Fangyu.exe";sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant();size=$file.Length})
}
$path=Join-Path $directory 'update.json'
[IO.File]::WriteAllText($path,($manifest|ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
$signer=Join-Path $PSScriptRoot '../../scripts/update-signing.ps1'
& $signer -ManifestPath $path -PrivateKeyPath $PrivateKeyPath
if(!$?){throw '签名失败，不能发布此更新。'}
& $signer -ManifestPath $path -VerifyOnly
if(!$?){throw '独立验签失败，不能发布此更新。'}
Write-Output '发布时必须同时上传 Fangyu.exe、update.json 和 update.json.sig，并将该桌面版本设为 Latest。'
