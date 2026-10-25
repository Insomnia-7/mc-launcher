#Requires -Version 5.1
[CmdletBinding()]
param(
    [string] $Dotnet = 'dotnet',
    [string] $OutputDirectory = '',
    [string[]] $RestoreSources = @(),
    [switch] $Lean,
    [switch] $Compress
)
$ErrorActionPreference = 'Stop'
$appRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $appRoot 'dist\desktop-win-x64'
}
$project = Join-Path $appRoot 'src\Launcher.Desktop\Launcher.Desktop.csproj'
$staging = Join-Path $appRoot ('scratch\publish-desktop-' + [Guid]::NewGuid().ToString('N'))
$profile = if ($Lean) { 'WindowsLean' } else { 'WindowsSingleFile' }
$publishArguments = @('publish', $project, '-c', 'Release', '-r', 'win-x64',
    '--self-contained', 'true', "-p:PublishProfile=$profile",
    '-p:DebugType=none', '-p:DebugSymbols=false',
    '-p:CopyOutputSymbolsToPublishDirectory=false', '-p:CopyDebugSymbolFilesFromPackages=false', '-o', $staging)
if ($Compress) { $publishArguments += '-p:EnableCompressionInSingleFile=true' }
foreach ($source in $RestoreSources) { $publishArguments += @('--source', $source) }
& $Dotnet @publishArguments
if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }
$files = @(Get-ChildItem -LiteralPath $staging -File -Recurse)
if ($files.Count -ne 1 -or $files[0].Name -ne 'MCLauncher.Desktop.exe') {
    throw "Expected exactly one EXE. Inspect publish output: $staging"
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$destination = Join-Path $OutputDirectory '方隅.exe'
Copy-Item -LiteralPath $files[0].FullName -Destination $destination -Force
$artifact = Get-Item -LiteralPath $destination
[pscustomobject]@{
    File = $artifact.FullName
    SizeMiB = [Math]::Round($artifact.Length / 1MB, 1)
    SHA256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
}
