<#
.SYNOPSIS
    为 MCLauncher 的自更新器（mclupd）生成 update.json。

.DESCRIPTION
    扫描一个「已发布目录」，给其中每个文件算小写 SHA-256 与字节数，按 path 升序写出更新清单。
    清单形状与 Launcher.Application.Update.UpdateManifestParser 严格对齐：

        {
          "version": "0.3.0",
          "notes": "说明" | null,
          "publishedAt": "2026-10-23T10:00:00+08:00",
          "files": [
            { "path": "updater/mclupd.exe", "url": "updater/mclupd.exe", "sha256": "<64 位小写 hex>", "size": 12345 }
          ]
        }

    规则：
      - path 永远是相对路径（/ 分隔），不含 ..、不以 / 开头、不含冒号 —— 更新器遇到这种路径会直接拒绝。
      - url：给了 -BaseUrl 就是 <BaseUrl>/<相对路径（逐段百分号编码）>；否则写相对路径，由客户端按
        清单所在位置解析（本地目录更新 / GitHub Release 都靠这个）。
      - 排除：*.pdb、任意 update.json、.backup 目录下的所有文件、*.log，以及 -OutFile 自身。
      - 输出固定：path 按 Ordinal 升序、UTF-8 无 BOM、2 空格缩进、LF 换行。
        同一份目录内容 + 同一个 -PublishedAt => 两次运行产出字节一致的 JSON（可直接比对哈希）。

.PARAMETER Directory
    已发布目录（必填）。里面的所有文件都会被收录，递归。

.PARAMETER Version
    写进清单的版本号（必填），例如 0.3.0 或 v0.3.0；客户端用 UpdateVersion 做宽松比较。

.PARAMETER Notes
    更新说明，可不填。不填写 null。

.PARAMETER BaseUrl
    下载基地址，例如 https://example.com/dl。给了就拼绝对 url（逐段 URL 编码）。
    不给就写相对路径。

.PARAMETER OutFile
    清单输出路径，默认是 <Directory>/update.json。所在目录不存在会自动创建。

.PARAMETER PublishedAt
    发布时间，ISO8601，例如 2026-10-23T10:00:00+08:00。不填取当前本地时间。
    需要「两次跑出字节一致的 JSON」时把它固定下来。

.PARAMETER FlattenAssetUrls
    只在给了 -BaseUrl 时有意义：url 用文件名而不是相对路径。
    GitHub Release 会把资产名拍平（out/updater/mclupd.exe 变成资产 mclupd.exe），
    所以 CI 里必须带上它，否则 updater/mclupd.exe 的下载地址会 404。
    用 nginx / S3 / 本地目录这类保留目录结构的地方就别带。

.EXAMPLE
    pwsh -File scripts/make-update-manifest.ps1 -Directory D:\tmp\out -Version 0.3.0 -Notes "测试"

.EXAMPLE
    pwsh -File scripts/make-update-manifest.ps1 -Directory D:\tmp\out -Version 0.3.0 `
        -BaseUrl https://example.com/dl -OutFile D:\tmp\out\update.json
#>
[CmdletBinding()]
param(
    [string] $Directory,
    [string] $Version,
    [string] $Notes,
    [string] $BaseUrl,
    [string] $OutFile,
    [string] $PublishedAt,
    [switch] $FlattenAssetUrls
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 统一的中文报错出口：一行说明 + exit 1，不往外抛栈
function Stop-WithError {
    param([Parameter(Mandatory = $true)][string] $Message)
    [Console]::Error.WriteLine("错误：$Message")
    exit 1
}

function ConvertTo-JsonString {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string] $Value)

    $builder = [System.Text.StringBuilder]::new()
    [void]$builder.Append([char]34)
    foreach ($character in $Value.ToCharArray()) {
        $code = [int] $character
        if ($character -eq [char]34) { [void]$builder.Append('\"') }
        elseif ($character -eq [char]92) { [void]$builder.Append('\\') }
        elseif ($code -eq 8) { [void]$builder.Append('\b') }
        elseif ($code -eq 12) { [void]$builder.Append('\f') }
        elseif ($code -eq 10) { [void]$builder.Append('\n') }
        elseif ($code -eq 13) { [void]$builder.Append('\r') }
        elseif ($code -eq 9) { [void]$builder.Append('\t') }
        elseif ($code -lt 32) { [void]$builder.Append('\u' + $code.ToString('x4')) }
        else { [void]$builder.Append($character) }
    }
    [void]$builder.Append([char]34)
    return $builder.ToString()
}

# 逐段编码：保留 / 作为层级分隔，空格、中文、+ 等全部百分号编码
function ConvertTo-UrlPath {
    param([Parameter(Mandatory = $true)][string] $Path)

    $segments = $Path.Split('/')
    $encoded = [System.Collections.Generic.List[string]]::new()
    foreach ($segment in $segments) {
        $encoded.Add([System.Uri]::EscapeDataString($segment))
    }
    return ($encoded -join '/')
}

# ---------- 参数校验 ----------

if ([string]::IsNullOrWhiteSpace($Directory)) {
    Stop-WithError '缺少 -Directory：要扫描的已发布目录（例如 D:\tmp-ci-manifest\out）。'
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    Stop-WithError '缺少 -Version：更新器靠它判断要不要升级，不能为空（例如 -Version 0.3.0）。'
}

$versionText = $Version.Trim()

$rootFull = [System.IO.Path]::GetFullPath($Directory)
if ([System.IO.File]::Exists($rootFull)) {
    Stop-WithError "-Directory 指向的是文件，不是目录：$rootFull"
}

if (-not [System.IO.Directory]::Exists($rootFull)) {
    Stop-WithError "目录不存在：$rootFull"
}

$rootFull = $rootFull.TrimEnd([char]'\', [char]'/')
if ($rootFull.Length -eq 0) {
    Stop-WithError "-Directory 不能是磁盘根目录（会把整盘扫进来）：$Directory"
}

# ---------- 输出位置 ----------

if ([string]::IsNullOrWhiteSpace($OutFile)) {
    $OutFile = [System.IO.Path]::Combine($rootFull, 'update.json')
}

$outFull = [System.IO.Path]::GetFullPath($OutFile)
$outDirectory = [System.IO.Path]::GetDirectoryName($outFull)
if ([string]::IsNullOrWhiteSpace($outDirectory)) {
    Stop-WithError "无法确定清单的输出目录：$outFull"
}

try {
    [void][System.IO.Directory]::CreateDirectory($outDirectory)
}
catch {
    Stop-WithError "创建输出目录失败：$outDirectory —— $($_.Exception.Message)"
}

# ---------- 下载基地址 ----------

$baseTrimmed = $null
if (-not [string]::IsNullOrWhiteSpace($BaseUrl)) {
    $candidate = $BaseUrl.Trim()
    if ($candidate -notmatch '^https?://') {
        Stop-WithError "「-BaseUrl」必须以 http:// 或 https:// 开头（本地目录更新不要传它，直接写相对路径）：$candidate"
    }
    $baseTrimmed = $candidate.TrimEnd('/')
}

if ($FlattenAssetUrls -and $null -eq $baseTrimmed) {
    Stop-WithError '「-FlattenAssetUrls」只在同时给了 -BaseUrl 时才有意义：没有 -BaseUrl 时 url 就是相对路径。'
}

# ---------- 发布时间 ----------

$published = [System.DateTimeOffset]::Now
if (-not [string]::IsNullOrWhiteSpace($PublishedAt)) {
    $parsed = [System.DateTimeOffset]::MinValue
    $styles = [System.Globalization.DateTimeStyles]::AssumeLocal
    $ok = [System.DateTimeOffset]::TryParse(
        $PublishedAt.Trim(),
        [System.Globalization.CultureInfo]::InvariantCulture,
        $styles,
        [ref] $parsed)
    if (-not $ok) {
        Stop-WithError "「-PublishedAt」不是合法时间（示例 2026-10-23T10:00:00+08:00）：$PublishedAt"
    }
    $published = $parsed
}

$publishedText = $published.ToString('yyyy-MM-ddTHH:mm:sszzz', [System.Globalization.CultureInfo]::InvariantCulture)

# ---------- 收集文件 ----------

try {
    $allFiles = [System.IO.Directory]::EnumerateFiles($rootFull, '*', [System.IO.SearchOption]::AllDirectories)
}
catch {
    Stop-WithError "遍历目录失败：$rootFull —— $($_.Exception.Message)"
}

$rootPrefix = $rootFull + [System.IO.Path]::DirectorySeparatorChar
$entries = [System.Collections.Generic.List[object]]::new()

foreach ($fullPath in $allFiles) {
    if (-not $fullPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        continue
    }

    $relative = $fullPath.Substring($rootPrefix.Length)
    if ($relative.Length -eq 0) {
        continue
    }

    $relativeUrl = $relative.Replace('\', '/')
    $segments = $relativeUrl.Split('/')
    $fileName = $segments[$segments.Length - 1]

    # 清单自身（不管叫 update.json 还是 -OutFile 指定的别的名字）永远不进清单，否则每次跑哈希都变
    if ([string]::Equals($fullPath, $outFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        continue
    }

    if ($fileName -ieq 'update.json' -or $fileName -like '*.sig') { continue }
    if ($fileName -like '*.pdb') { continue }
    if ($fileName -like '*.log') { continue }

    if ($segments.Length -gt 1) {
        $insideBackup = $false
        for ($index = 0; $index -lt ($segments.Length - 1); $index++) {
            if ($segments[$index] -ieq '.backup') { $insideBackup = $true; break }
        }
        if ($insideBackup) { continue }
    }

    try {
        $sha256 = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $size = [System.IO.FileInfo]::new($fullPath).Length
    }
    catch {
        Stop-WithError "读取文件失败（可能被其它进程占用）：$relativeUrl —— $($_.Exception.Message)"
    }

    $urlPath = $relativeUrl
    if ($FlattenAssetUrls) { $urlPath = $fileName }

    $urlText = ConvertTo-UrlPath -Path $urlPath
    if ($null -ne $baseTrimmed) {
        $urlText = $baseTrimmed + '/' + $urlText
    }

    $entries.Add([pscustomobject]@{
            Path   = $relativeUrl
            Url    = $urlText
            Sha256 = $sha256
            Size   = [long] $size
        })
}

if ($entries.Count -eq 0) {
    Stop-WithError "目录里没有可发布的文件（已排除 *.pdb、update.json、.backup 目录、*.log）：$rootFull"
}

# 固定顺序：Ordinal 比较，跨机器/跨文化一致
$entries.Sort([System.Comparison[object]] {
        param($left, $right)
        [string]::CompareOrdinal($left.Path, $right.Path)
    })

# ---------- 拼 JSON ----------

$newLine = [string] [char] 10
$builder = [System.Text.StringBuilder]::new()

[void] $builder.Append('{').Append($newLine)
[void] $builder.Append('  "version": ').Append((ConvertTo-JsonString -Value $versionText)).Append(',').Append($newLine)
[void] $builder.Append('  "notes": ')
if ([string]::IsNullOrWhiteSpace($Notes)) {
    [void] $builder.Append('null')
}
else {
    [void] $builder.Append((ConvertTo-JsonString -Value $Notes))
}
[void] $builder.Append(',').Append($newLine)
[void] $builder.Append('  "publishedAt": ').Append((ConvertTo-JsonString -Value $publishedText)).Append(',').Append($newLine)
[void] $builder.Append('  "files": [').Append($newLine)

$totalBytes = [long] 0
for ($index = 0; $index -lt $entries.Count; $index++) {
    $entry = $entries[$index]
    $totalBytes += $entry.Size

    [void] $builder.Append('    {').Append($newLine)
    [void] $builder.Append('      "path": ').Append((ConvertTo-JsonString -Value $entry.Path)).Append(',').Append($newLine)
    [void] $builder.Append('      "url": ').Append((ConvertTo-JsonString -Value $entry.Url)).Append(',').Append($newLine)
    [void] $builder.Append('      "sha256": ').Append((ConvertTo-JsonString -Value $entry.Sha256)).Append(',').Append($newLine)
    [void] $builder.Append('      "size": ').Append($entry.Size.ToString([System.Globalization.CultureInfo]::InvariantCulture)).Append($newLine)
    [void] $builder.Append('    }')
    if ($index -lt ($entries.Count - 1)) { [void] $builder.Append(',') }
    [void] $builder.Append($newLine)
}

[void] $builder.Append('  ]').Append($newLine)
[void] $builder.Append('}').Append($newLine)

$json = $builder.ToString()

try {
    [System.IO.File]::WriteAllText($outFull, $json, [System.Text.UTF8Encoding]::new($false))
}
catch {
    Stop-WithError "写清单失败：$outFull —— $($_.Exception.Message)"
}

# ---------- 报告 ----------

if ($null -eq $baseTrimmed) {
    $urlMode = '相对路径（客户端按清单所在位置解析）'
}
elseif ($FlattenAssetUrls) {
    $urlMode = "绝对地址，资产名拍平：$baseTrimmed/<文件名>"
}
else {
    $urlMode = "绝对地址，保留目录：$baseTrimmed/<相对路径>"
}

Write-Host "清单已生成：$outFull"
Write-Host "  版本      ：$versionText"
Write-Host "  文件数    ：$($entries.Count)"
Write-Host "  总字节    ：$totalBytes"
Write-Host "  发布时间  ：$publishedText"
Write-Host "  url 形式  ：$urlMode"
Write-Host '  文件列表（按 path 升序）：'
foreach ($entry in $entries) {
    $short = $entry.Sha256.Substring(0, 12)
    Write-Host ("    + " + $entry.Path + "  " + $entry.Size + " B  sha256 " + $short + "...")
}
