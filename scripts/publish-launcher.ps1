#Requires -Version 7.0
[CmdletBinding()]
param(
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '../mc-launcher/dist/portable'),
    [string] $Version
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $output) -and (Get-ChildItem -LiteralPath $output -Force | Select-Object -First 1)) { throw '输出目录必须为空，避免混入旧文件。' }
$dotnet = if ($env:DOTNET_ROOT -and (Test-Path (Join-Path $env:DOTNET_ROOT 'dotnet.exe'))) { Join-Path $env:DOTNET_ROOT 'dotnet.exe' } else { (Get-Command dotnet).Source }
$project = Join-Path $repo 'mc-launcher/src/Launcher.App/Launcher.App.csproj'
if (-not $Version) { $Version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version }
if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$') { throw '版本号格式无效。' }
$payload = Join-Path $repo ('mc-launcher/src/Launcher.App/obj/launcher-publish/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $payload -Force | Out-Null
& $dotnet publish (Join-Path $repo 'mc-launcher/tools/Launcher.Updater/Launcher.Updater.csproj') -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false "-p:Version=$Version" -o $payload
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$updater = Join-Path $payload 'mclupd.exe'
& $dotnet publish $project -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false "-p:UpdaterPayload=$updater" "-p:Version=$Version" "-p:FileVersion=$($Version.Split('-')[0]).0" -o $output
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$files = @(Get-ChildItem -LiteralPath $output -File -Recurse)
if ($files.Count -ne 1 -or $files[0].Name -ne 'MCLauncher.exe') { throw '发布结果不是单个 MCLauncher.exe。' }
Write-Host "单文件产物：$($files[0].FullName)，$($files[0].Length) 字节；需要 .NET 10 Desktop Runtime。"
