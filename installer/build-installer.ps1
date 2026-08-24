param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Version = '2.0.0',
    [string]$InnoCompilerPath,
    [Parameter(Mandatory = $true)]
    [string]$ExtensionTrustAnchorPath,
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$stagingRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '.staging'))
$appPublish = Join-Path $stagingRoot 'app'
$bundleStaging = Join-Path $stagingRoot 'bundle'
$dist = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'dist'))
$signatureVerifier = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'verify-ed25519.ps1'))
$projectAssetsPath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'src\HephaestusWorkbench.Services\obj\project.assets.json'))
$maximumPackageBytes = 209715200
$knownKinds = @('workspace', 'analysis', 'maintenance')
if ($Version -cnotmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
    throw "发布版本必须使用 X.Y.Z 三段式正式版本：$Version。"
}

function Assert-ChildPath([string]$Parent, [string]$Child, [string]$Description) {
    $parentPath = [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetFullPath($Parent))
    $childPath = [System.IO.Path]::GetFullPath($Child)
    if (-not $childPath.StartsWith($parentPath + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description 必须位于 $parentPath 内：$childPath"
    }
}

function Assert-ExactProperties([object]$Value, [string[]]$Allowed, [string]$Description) {
    if ($null -eq $Value) { throw "$Description 不能为空。" }
    $actual = @($Value.PSObject.Properties.Name)
    $unknown = @($actual | Where-Object { $Allowed -cnotcontains $_ })
    $missing = @($Allowed | Where-Object { $actual -cnotcontains $_ })
    if ($unknown.Count -gt 0 -or $missing.Count -gt 0) {
        throw "$Description 字段不符合 schema v2。未知：$($unknown -join '、')；缺失：$($missing -join '、')。"
    }
}

function Test-SemanticVersion([object]$Value) {
    if ($Value -isnot [string]) { return $false }
    return $Value -cmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-((?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$'
}

function Compare-SemanticCore([string]$Left, [string]$Right) {
    $leftCore = ($Left -split '[+-]', 2)[0].Split('.')
    $rightCore = ($Right -split '[+-]', 2)[0].Split('.')
    for ($index = 0; $index -lt 3; $index++) {
        $leftPart = [System.Numerics.BigInteger]::Parse($leftCore[$index])
        $rightPart = [System.Numerics.BigInteger]::Parse($rightCore[$index])
        $comparison = $leftPart.CompareTo($rightPart)
        if ($comparison -ne 0) { return $comparison }
    }
    return 0
}

# 正式安装包的扩展签名信任边界只能由发布管线显式注入，绝不从源码或网络回退。
if ([string]::IsNullOrWhiteSpace($ExtensionTrustAnchorPath)) {
    throw '必须通过 -ExtensionTrustAnchorPath 显式提供正式扩展信任锚。'
}
$extensionTrustAnchorPath = [System.IO.Path]::GetFullPath($ExtensionTrustAnchorPath)
if (-not (Test-Path -LiteralPath $extensionTrustAnchorPath -PathType Leaf)) {
    throw "未找到正式扩展信任锚：$extensionTrustAnchorPath"
}
try {
    $trustAnchorDocument = Get-Content -LiteralPath $extensionTrustAnchorPath -Raw -Encoding UTF8 | ConvertFrom-Json
}
catch {
    throw "正式扩展信任锚不是有效 JSON：$($_.Exception.Message)"
}
Assert-ExactProperties $trustAnchorDocument @('schemaVersion', 'trustedPublishers') '正式扩展信任锚'
if ($trustAnchorDocument.schemaVersion -isnot [long] -or $trustAnchorDocument.schemaVersion -ne 2) {
    throw '正式扩展信任锚 schemaVersion 必须是整数 2。'
}
if ($trustAnchorDocument.trustedPublishers -isnot [System.Array]) {
    throw '正式扩展信任锚 trustedPublishers 必须是 JSON 数组。'
}
$trustedPublishers = @($trustAnchorDocument.trustedPublishers)
if ($trustedPublishers.Count -eq 0) {
    throw '正式扩展信任锚 trustedPublishers 不能为空。'
}

$trustedPublishersByKeyId = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::Ordinal)
foreach ($publisher in $trustedPublishers) {
    Assert-ExactProperties $publisher @('keyId', 'publisherId', 'publicKey', 'scope') '正式扩展信任锚 trustedPublishers 项'
    Assert-ExactProperties $publisher.scope @('allowedKinds', 'permissions') '正式扩展信任锚 scope'
    if ($publisher.keyId -isnot [string] -or [string]::IsNullOrWhiteSpace($publisher.keyId)) {
        throw '正式扩展信任锚 keyId 必须是非空字符串。'
    }
    if (-not $trustedPublishersByKeyId.TryAdd([string]$publisher.keyId, $publisher)) {
        throw "正式扩展信任锚存在重复 keyId：$($publisher.keyId)。"
    }
    if ($publisher.publisherId -isnot [string] -or [string]::IsNullOrWhiteSpace($publisher.publisherId)) {
        throw "正式扩展信任锚 keyId $($publisher.keyId) 的 publisherId 必须是非空字符串。"
    }
    if ($publisher.publicKey -isnot [string] -or
        $publisher.publicKey -cnotmatch '^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$') {
        throw "正式扩展信任锚 keyId $($publisher.keyId) 的 publicKey 必须是有效 Base64。"
    }
    try { $publicKeyBytes = [System.Convert]::FromBase64String($publisher.publicKey) }
    catch { throw "正式扩展信任锚 keyId $($publisher.keyId) 的 publicKey 必须是有效 Base64。" }
    if ($publicKeyBytes.Length -ne 32) {
        throw "正式扩展信任锚 keyId $($publisher.keyId) 的 publicKey 必须解码为 32 字节。"
    }
    if ($publisher.scope.allowedKinds -isnot [System.Array]) {
        throw "正式扩展信任锚 keyId $($publisher.keyId) 的 allowedKinds 必须是 JSON 数组。"
    }
    $allowedKinds = @($publisher.scope.allowedKinds)
    if ($allowedKinds.Count -eq 0 -or @($allowedKinds | Where-Object { $_ -isnot [string] -or $knownKinds -cnotcontains $_ }).Count -gt 0) {
        throw "正式扩展信任锚 keyId $($publisher.keyId) 的 allowedKinds 必须包含受支持的扩展类型。"
    }
    if ($publisher.scope.permissions -isnot [System.Array] -or
        @($publisher.scope.permissions | Where-Object { $_ -isnot [string] -or [string]::IsNullOrWhiteSpace($_) }).Count -gt 0) {
        throw "正式扩展信任锚 keyId $($publisher.keyId) 的 permissions 必须是字符串数组。"
    }
}
$bundleManifestPath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'distribution\bundled-extensions.json'))
if (-not (Test-Path -LiteralPath $bundleManifestPath -PathType Leaf)) {
    throw "未找到 Bundled Extension 锁定清单：$bundleManifestPath"
}
if ((Get-Item -LiteralPath $bundleManifestPath).Attributes.HasFlag([System.IO.FileAttributes]::ReparsePoint)) {
    throw "Bundled Extension 锁定清单不能是重解析点：$bundleManifestPath"
}

try {
    $bundleDocument = Get-Content -LiteralPath $bundleManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
}
catch {
    throw "Bundled Extension 锁定清单不是有效 JSON：$($_.Exception.Message)"
}
Assert-ExactProperties $bundleDocument @('schemaVersion', 'extensions') 'Bundled Extension 锁定清单'
if ($bundleDocument.schemaVersion -isnot [long] -or $bundleDocument.schemaVersion -ne 2) {
    throw 'Bundled Extension 锁定清单 schemaVersion 必须是整数 2。'
}
if ($bundleDocument.extensions -isnot [System.Array]) {
    throw 'Bundled Extension 锁定清单 extensions 必须是 JSON 数组。'
}
$bundledExtensions = @($bundleDocument.extensions)
if ($bundledExtensions.Count -eq 0) {
    throw 'Bundled Extension 锁定清单 extensions 不能为空。'
}

$ids = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
$assets = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$WindowsReservedNames = @('CON', 'PRN', 'AUX', 'NUL', 'COM1', 'COM2', 'COM3', 'COM4', 'COM5', 'COM6', 'COM7', 'COM8', 'COM9', 'LPT1', 'LPT2', 'LPT3', 'LPT4', 'LPT5', 'LPT6', 'LPT7', 'LPT8', 'LPT9', 'COM¹', 'COM²', 'COM³', 'LPT¹', 'LPT²', 'LPT³')
$invalidFileNameChars = [System.IO.Path]::GetInvalidFileNameChars()
foreach ($item in $bundledExtensions) {
    Assert-ExactProperties $item @('id', 'name', 'description', 'publisherId', 'kind', 'asset', 'release') "Bundled Extension 项"
    if ($item.id -isnot [string] -or $item.id -cnotmatch '^[a-z0-9]+(?:-[a-z0-9]+)*$' -or -not $ids.Add($item.id)) {
        throw "Bundled Extension ID 无效或重复：$($item.id)。"
    }
    if ($item.name -isnot [string] -or [string]::IsNullOrWhiteSpace($item.name) -or
        $item.description -isnot [string] -or [string]::IsNullOrWhiteSpace($item.description)) {
        throw "Bundled Extension $($item.id) 的 name 和 description 必须是非空字符串。"
    }
    if ($item.publisherId -isnot [string] -or $item.publisherId -cnotmatch '^[a-z0-9]+(?:-[a-z0-9]+)*$') {
        throw "Bundled Extension $($item.id) 的 publisherId 无效。"
    }
    if ($item.kind -isnot [string] -or $knownKinds -cnotcontains $item.kind) {
        throw "Bundled Extension $($item.id) 的 kind 无效：$($item.kind)。"
    }

    $assetName = $item.asset
    $deviceName = if ($assetName -is [string]) { [System.IO.Path]::GetFileNameWithoutExtension($assetName).Split('.', 2)[0].TrimEnd(' ', '.') } else { '' }
    if ($assetName -isnot [string] -or [string]::IsNullOrWhiteSpace($assetName) -or
        $assetName.Contains('/') -or $assetName.Contains('\') -or $assetName.Contains('..') -or
        [System.IO.Path]::IsPathRooted($assetName) -or
        $assetName -ne [System.IO.Path]::GetFileName($assetName) -or
        -not $assetName.EndsWith('.zip', [System.StringComparison]::OrdinalIgnoreCase) -or
        $assetName.IndexOfAny($invalidFileNameChars) -ge 0 -or
        $WindowsReservedNames -contains $deviceName -or
        -not $assets.Add($assetName)) {
        throw "Bundled Extension asset 必须是唯一的安全 ZIP 文件名：$assetName。"
    }

    $release = $item.release
    Assert-ExactProperties $release @('version', 'minHostVersion', 'url', 'size', 'sha256', 'signature') "Bundled Extension $($item.id) release"
    Assert-ExactProperties $release.signature @('keyId', 'signature') "Bundled Extension $($item.id) Ed25519 签名"
    if (-not (Test-SemanticVersion $release.version) -or -not (Test-SemanticVersion $release.minHostVersion)) {
        throw "Bundled Extension $($item.id) 的 version 或 minHostVersion 无效。"
    }
    if ((Compare-SemanticCore $release.minHostVersion $Version) -gt 0) {
        throw "Bundled Extension $($item.id) 的最低宿主版本 $($release.minHostVersion) 高于安装包版本 $Version。"
    }
    if ($release.size -isnot [long]) {
        throw "Bundled Extension $($item.id) 的 release.size 必须是 JSON 整数。"
    }
    $size = $release.size
    if ($size -le 0 -or $size -gt $maximumPackageBytes) {
        throw "Bundled Extension $($item.id) 的 release.size 必须在 1 到 $maximumPackageBytes 字节之间。"
    }
    if ($release.sha256 -isnot [string] -or $release.sha256 -cnotmatch '^[0-9a-fA-F]{64}$') {
        throw "Bundled Extension $($item.id) 的 SHA-256 无效。"
    }
    $downloadUri = $null
    if ($release.url -isnot [string] -or -not [System.Uri]::TryCreate($release.url, [System.UriKind]::Absolute, [ref]$downloadUri) -or $downloadUri.Scheme -ne 'https') {
        throw "Bundled Extension $($item.id) 的下载 URL 必须是绝对 HTTPS 地址。"
    }
    if ($release.signature.keyId -isnot [string] -or [string]::IsNullOrWhiteSpace($release.signature.keyId) -or
        $release.signature.signature -isnot [string] -or [string]::IsNullOrWhiteSpace($release.signature.signature)) {
        throw "Bundled Extension $($item.id) 缺少 Ed25519 keyId 或签名。"
    }
    try { $signatureBytes = [System.Convert]::FromBase64String($release.signature.signature) }
    catch { throw "Bundled Extension $($item.id) 的 Ed25519 签名不是有效 Base64。" }
    if ($signatureBytes.Length -ne 64) {
        throw "Bundled Extension $($item.id) 的 Ed25519 签名必须为 64 字节。"
    }
}

# Bundled Extension 必须由正式信任锚中的发布者签名，避免安装包在首次启动时拒绝自身离线扩展。
foreach ($item in $bundledExtensions) {
    $keyId = [string]$item.release.signature.keyId
    if (-not $trustedPublishersByKeyId.ContainsKey($keyId)) {
        throw "Bundled Extension $($item.id) 的 Ed25519 keyId 不在正式扩展信任锚中：$keyId。"
    }
    $trustedPublisher = $trustedPublishersByKeyId[$keyId]
    if ([string]$item.publisherId -cne [string]$trustedPublisher.publisherId) {
        throw "Bundled Extension $($item.id) 的 publisherId 与正式扩展信任锚不一致。"
    }
    if (@($trustedPublisher.scope.allowedKinds) -cnotcontains [string]$item.kind) {
        throw "Bundled Extension $($item.id) 的 kind 不在正式扩展信任锚 allowedKinds 范围内。"
    }
}
# ValidateOnly 也必须完成每一个扩展及其嵌套 release/signature 的全部契约校验后才能返回。
if ($ValidateOnly) {
    Write-Host 'Bundled Extension 锁定清单契约校验通过，正式扩展信任锚匹配。'
    return
}

if ([string]::IsNullOrWhiteSpace($InnoCompilerPath)) {
    $compilerCandidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )
    $InnoCompilerPath = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($InnoCompilerPath) -or -not (Test-Path -LiteralPath $InnoCompilerPath -PathType Leaf)) {
    throw '未找到 Inno Setup 6 编译器 ISCC.exe，请先安装 Inno Setup 6，或通过 -InnoCompilerPath 显式传入。'
}
if (-not (Test-Path -LiteralPath $signatureVerifier -PathType Leaf)) {
    throw "缺少 Bundled Extension Ed25519 发布预检脚本：$signatureVerifier"
}

$env:DOTNET_CLI_HOME = Join-Path $repoRoot '.dotnet-home'
$appProject = Join-Path $repoRoot 'src\HephaestusWorkbench.App\HephaestusWorkbench.App.csproj'
$innoScript = Join-Path $PSScriptRoot 'HephaestusWorkbench.iss'
Write-Host '正在还原 win-x64 发布依赖……'
& dotnet restore $appProject -r win-x64 --configfile (Join-Path $repoRoot 'NuGet.config')
if ($LASTEXITCODE -ne 0) { throw "应用还原失败，退出码：$LASTEXITCODE" }

Assert-ChildPath $PSScriptRoot $stagingRoot '安装器暂存目录'
Assert-ChildPath $PSScriptRoot $dist '安装器输出目录'
if (Test-Path -LiteralPath $stagingRoot) { Remove-Item -LiteralPath $stagingRoot -Recurse -Force }
if (Test-Path -LiteralPath $dist) {
    try {
        Remove-Item -LiteralPath $dist -Recurse -Force -ErrorAction Stop
    }
    catch {
        $dist = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ("dist-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))))
        Assert-ChildPath $PSScriptRoot $dist '安装器备用输出目录'
        Write-Warning "原 dist 目录正在被占用，改用独立输出目录：$dist"
    }
}
New-Item -ItemType Directory -Force -Path $appPublish, $bundleStaging, $dist | Out-Null

Write-Host '正在下载并校验锁定的 Bundled Extension 资产……'
foreach ($item in $bundledExtensions) {
    $assetName = [string]$item.asset
    $release = $item.release
    $assetPath = Join-Path $bundleStaging $assetName
    Assert-ChildPath $bundleStaging $assetPath "Bundled Extension $($item.id) 资产"
    Invoke-WebRequest -Uri ([string]$release.url) -OutFile $assetPath -TimeoutSec 120

    $file = Get-Item -LiteralPath $assetPath
    if ($file.Attributes.HasFlag([System.IO.FileAttributes]::ReparsePoint)) {
        throw "下载后的 Bundled Extension 资产不能是重解析点：$assetPath"
    }
    if ($file.Length -ne [long]$release.size) {
        throw "Bundled Extension $($item.id) 大小校验失败：实际 $($file.Length)，清单 $($release.size)。"
    }
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $assetPath).Hash.ToLowerInvariant()
    if ($hash -ne ([string]$release.sha256).ToLowerInvariant()) {
        throw "Bundled Extension $($item.id) SHA-256 校验失败：实际 $hash。"
    }

    $trustedPublisher = $trustedPublishersByKeyId[[string]$release.signature.keyId]
    & $signatureVerifier `
        -PackagePath $assetPath `
        -PublicKeyBase64 ([string]$trustedPublisher.publicKey) `
        -SignatureBase64 ([string]$release.signature.signature) `
        -ProjectAssetsPath $projectAssetsPath
    Write-Host "Bundled Extension $($item.id) 资产校验通过：$assetName，SHA-256 $hash，Ed25519 签名有效"
}
Copy-Item -LiteralPath $bundleManifestPath -Destination (Join-Path $bundleStaging 'bundled-extensions.json')

Write-Host '正在发布必须携带 BundledExtensions 的 self-contained 主程序……'
& dotnet publish $appProject -c $Configuration -r win-x64 --self-contained true --no-restore -p:Version=$Version -p:RequireBundledExtensions=true -p:ExtensionTrustAnchorPath=$ExtensionTrustAnchorPath -p:DebugType=None -p:DebugSymbols=false -o $appPublish
if ($LASTEXITCODE -ne 0) { throw "应用发布失败，退出码：$LASTEXITCODE" }
Copy-Item -LiteralPath $bundleStaging -Destination (Join-Path $appPublish 'BundledExtensions') -Recurse

Write-Host '正在生成标准单文件离线安装包……'
& $InnoCompilerPath "/DMyAppVersion=$Version" "/DAppSource=$appPublish" "/DOutputDir=$dist" $innoScript
if ($LASTEXITCODE -ne 0) { throw "Inno Setup 编译失败，退出码：$LASTEXITCODE" }
$setupFileName = "HephaestusWorkbench_v$Version.exe"
$setupExecutable = Join-Path $dist $setupFileName
if (-not (Test-Path -LiteralPath $setupExecutable -PathType Leaf)) {
    throw "未生成预期的安装包：$setupExecutable"
}

$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $setupExecutable).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllLines(
    (Join-Path $dist 'SHA256SUMS.txt'),
    @("$hash  $setupFileName"),
    [System.Text.UTF8Encoding]::new($false))

Assert-ChildPath $PSScriptRoot $stagingRoot '安装器暂存目录'
Remove-Item -LiteralPath $stagingRoot -Recurse -Force
Write-Host "标准单文件离线安装包已生成：$setupExecutable"
