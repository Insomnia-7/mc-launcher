#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $PrivateKeyPath,
    [string] $TrustStorePath = (Join-Path $PSScriptRoot '../mc-launcher/src/Launcher.Application/Update/UpdateTrustKeys.json')
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw '本机备份使用 Windows DPAPI，请在 Windows 上创建密钥。' }
$privatePath = [IO.Path]::GetFullPath($PrivateKeyPath)
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
if ($privatePath.StartsWith($repository, [StringComparison]::OrdinalIgnoreCase)) { throw '私钥备份必须保存在仓库之外。' }
if (Test-Path -LiteralPath $privatePath) { throw '私钥备份已经存在，不会覆盖。' }
$keys = if (Test-Path -LiteralPath $TrustStorePath) { Get-Content -LiteralPath $TrustStorePath -Raw | ConvertFrom-Json -AsHashtable } else { @{} }
$rsa = [Security.Cryptography.RSA]::Create(3072)
$private = $null
try {
    $public = $rsa.ExportSubjectPublicKeyInfo()
    $id = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($public)).ToLowerInvariant()
    $private = $rsa.ExportPkcs8PrivateKey()
    $encrypted = [Security.Cryptography.ProtectedData]::Protect($private, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($privatePath)) | Out-Null
    $stream = [IO.File]::Open($privatePath, [IO.FileMode]::CreateNew)
    try { $stream.Write($encrypted) } finally { $stream.Dispose() }
    $keys[$id] = [Convert]::ToBase64String($public)
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($TrustStorePath), ($keys | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    Write-Host "已创建 RSA-3072 更新签名密钥，公钥 ID：$id"
    Write-Host '私钥已按当前 Windows 用户加密保存；备份不能直接由另一台电脑或另一用户解密。'
} finally {
    if ($null -ne $private) { [Array]::Clear($private, 0, $private.Length) }
    $rsa.Dispose()
}
