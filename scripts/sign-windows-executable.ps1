#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string[]] $Path)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($env:PFX_BASE64)) { throw '缺少代码签名证书。' }
$pfx = Join-Path $env:RUNNER_TEMP ('code-signing-' + [Guid]::NewGuid().ToString('N') + '.pfx')
try {
    [IO.File]::WriteAllBytes($pfx, [Convert]::FromBase64String(($env:PFX_BASE64 -replace '\s', '')))
    $stamp = if ([string]::IsNullOrWhiteSpace($env:TIMESTAMP_URL)) { 'https://timestamp.digicert.com' } else { $env:TIMESTAMP_URL }
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object { try { [version](Split-Path (Split-Path (Split-Path $_.FullName -Parent) -Parent) -Leaf) } catch { [version]'0.0' } } -Descending |
        Select-Object -First 1 -ExpandProperty FullName
    if (-not $signtool) { $signtool = 'signtool' }
    foreach ($exe in $Path) {
        if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw '缺少待签名文件。' }
        & $signtool sign /fd SHA256 /tr $stamp /td SHA256 /f $pfx /p $env:PFX_PASSWORD $exe
        if ($LASTEXITCODE -ne 0) { throw '可执行文件签名失败。' }
        & $signtool verify /pa /v $exe
        if ($LASTEXITCODE -ne 0) { throw '可执行文件签名验证失败。' }
    }
} finally { if (Test-Path -LiteralPath $pfx) { Remove-Item -LiteralPath $pfx -Force } }
