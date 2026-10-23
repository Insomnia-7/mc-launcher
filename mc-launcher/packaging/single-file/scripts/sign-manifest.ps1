<#
.SYNOPSIS
    生成 / 使用一把测试用的 update.json 签名密钥（rsa-pss-sha256）。

.DESCRIPTION
    与 scripts/update-signing.ps1 的协议一致（envelope: schema/algorithm/keyId/signature），
    区别只在于私钥是本地生成的文件，而不是 CI secret。

    -GenKey 会把公钥写进 -TrustKeysPath 指向的 UpdateTrustKeys.json（keyId → SPKI base64）。
    **这个文件必须指向隔离副本里的那份**，不要改仓库里的信任配置。

.EXAMPLE
    .\sign-manifest.ps1 -GenKey -TrustKeysPath D:\tmp\src\src\Launcher.Application\Update\UpdateTrustKeys.json
    .\sign-manifest.ps1 -Sign -ManifestPath D:\tmp\staging\update.json
#>
[CmdletBinding()]
param(
    [switch]$GenKey,
    [switch]$Sign,
    [string]$KeysDir = (Join-Path $env:TEMP 'mc-single-file\keys'),
    [string]$ManifestPath,
    [string]$TrustKeysPath
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $KeysDir | Out-Null
$keyFile = Join-Path $KeysDir 'test-signing.pkcs8.b64'

if ($GenKey) {
    if (-not $TrustKeysPath) { throw '-GenKey 需要 -TrustKeysPath（隔离副本里的 UpdateTrustKeys.json）' }
    $rsa = [System.Security.Cryptography.RSA]::Create(3072)
    $pkcs8 = [Convert]::ToBase64String($rsa.ExportPkcs8PrivateKey())
    $spki = $rsa.ExportSubjectPublicKeyInfo()
    $keyId = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($spki)).ToLowerInvariant()
    [IO.File]::WriteAllText($keyFile, $pkcs8)
    [IO.File]::WriteAllText($TrustKeysPath, "{`n  `"$keyId`": `"$([Convert]::ToBase64String($spki))`"`n}`n")
    Write-Host "keyId  = $keyId"
    Write-Host "私钥   = $keyFile"
    Write-Host "信任   = $TrustKeysPath"
    $rsa.Dispose()
    return
}

if ($Sign) {
    if (-not $ManifestPath) { throw '-Sign 需要 -ManifestPath' }
    $rsa = [System.Security.Cryptography.RSA]::Create()
    $rsa.ImportPkcs8PrivateKey([Convert]::FromBase64String([IO.File]::ReadAllText($keyFile)), [ref]$null)
    $spki = $rsa.ExportSubjectPublicKeyInfo()
    $keyId = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($spki)).ToLowerInvariant()
    $bytes = [IO.File]::ReadAllBytes($ManifestPath)
    $sig = $rsa.SignData($bytes, [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.RSASignaturePadding]::Pss)
    $envelope = [ordered]@{
        schema    = 1
        algorithm = 'rsa-pss-sha256'
        keyId     = $keyId
        signature = [Convert]::ToBase64String($sig)
    } | ConvertTo-Json -Compress
    [IO.File]::WriteAllText($ManifestPath + '.sig', $envelope)
    Write-Host "signed  = $ManifestPath ($($bytes.Length) bytes)"
    Write-Host "sigfile = $ManifestPath.sig ($((Get-Item ($ManifestPath + '.sig')).Length) bytes)"
    $rsa.Dispose()
    return
}

Write-Host 'usage: sign-manifest.ps1 -GenKey -TrustKeysPath <path> | -Sign -ManifestPath <path>'
