#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ManifestPath,
    [string] $PrivateKeyPath,
    [string] $TrustStorePath = (Join-Path $PSScriptRoot '../mc-launcher/src/Launcher.Application/Update/UpdateTrustKeys.json'),
    [switch] $VerifyOnly
)
$ErrorActionPreference = 'Stop'
$private = $null
$rsa = [Security.Cryptography.RSA]::Create()
try {
    $manifest = [IO.File]::ReadAllBytes([IO.Path]::GetFullPath($ManifestPath))
    if ($manifest.Length -gt 1048576) { throw '清单超过大小限制。' }
    $keys = Get-Content -LiteralPath $TrustStorePath -Raw | ConvertFrom-Json -AsHashtable
    $signaturePath = [IO.Path]::GetFullPath($ManifestPath) + '.sig'
    if ($VerifyOnly) {
        if ((Get-Item -LiteralPath $signaturePath).Length -gt 16384) { throw '签名超过大小限制。' }
        $envelope = Get-Content -LiteralPath $signaturePath -Raw | ConvertFrom-Json
        if ($envelope.schema -ne 1 -or $envelope.algorithm -cne 'rsa-pss-sha256' -or -not $keys.ContainsKey($envelope.keyId)) { throw '未知签名或公钥。' }
        $public = [Convert]::FromBase64String($keys[$envelope.keyId])
        $read = 0
        $rsa.ImportSubjectPublicKeyInfo($public, [ref]$read)
        $id = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($public)).ToLowerInvariant()
        if ($read -ne $public.Length -or $id -cne $envelope.keyId -or $rsa.KeySize -lt 3072) { throw '公钥不符合要求。' }
        $signature = [Convert]::FromBase64String($envelope.signature)
    } else {
        if (-not [string]::IsNullOrWhiteSpace($PrivateKeyPath)) {
            if (-not $IsWindows) { throw 'DPAPI 备份只能在对应 Windows 用户下读取。' }
            $private = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes([IO.Path]::GetFullPath($PrivateKeyPath)), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
        } elseif (-not [string]::IsNullOrWhiteSpace($env:MCLAUNCHER_UPDATE_SIGNING_KEY)) {
            $private = [Convert]::FromBase64String($env:MCLAUNCHER_UPDATE_SIGNING_KEY)
        } else { throw '缺少发布签名私钥，不能生成可发布的更新。' }
        $read = 0
        $rsa.ImportPkcs8PrivateKey($private, [ref]$read)
        if ($read -ne $private.Length -or $rsa.KeySize -lt 3072) { throw '私钥格式不符合要求。' }
        $public = $rsa.ExportSubjectPublicKeyInfo()
        $id = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($public)).ToLowerInvariant()
        if (-not $keys.ContainsKey($id) -or $keys[$id] -cne [Convert]::ToBase64String($public)) { throw '私钥没有对应的内置受信公钥。' }
        $signature = $rsa.SignData($manifest, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pss)
        $envelope = [ordered]@{ schema = 1; algorithm = 'rsa-pss-sha256'; keyId = $id; signature = [Convert]::ToBase64String($signature) }
    }
    if (-not $rsa.VerifyData($manifest, $signature, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pss)) { throw '签名验证失败。' }
    if (-not $VerifyOnly) { [IO.File]::WriteAllText($signaturePath, ($envelope | ConvertTo-Json), [Text.UTF8Encoding]::new($false)) }
    Write-Host "更新签名验证通过，公钥 ID：$id"
} catch {
    # 不输出私钥、环境变量或调用表达式。
    [Console]::Error.WriteLine('更新签名操作失败：请检查密钥、公钥配置及清单/签名文件。')
    exit 1
} finally {
    if ($null -ne $private) { [Array]::Clear($private, 0, $private.Length) }
    $rsa.Dispose()
}
