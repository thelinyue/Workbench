param(
    [Parameter(Mandatory = $true)]
    [string]$ReleaseMetadataPath,
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,
    [Parameter(Mandatory = $true)]
    [string]$ExtensionId,
    [Parameter(Mandatory = $true)]
    [string]$ReviewedDescription,
    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$maximumPackageBytes = 209715200

function Assert-InputFile([string]$Path, [string]$Description) {
    $resolved = [System.IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "未找到$Description：$resolved"
    }

    $item = Get-Item -LiteralPath $resolved
    if ($item.Attributes.HasFlag([System.IO.FileAttributes]::ReparsePoint)) {
        throw "$Description不能是重解析点：$resolved"
    }

    return $resolved
}

function Read-JsonObject([string]$Path, [string]$Description) {
    try {
        $options = [System.Text.Json.JsonDocumentOptions]::new()
        $options.AllowTrailingCommas = $false
        $options.CommentHandling = [System.Text.Json.JsonCommentHandling]::Disallow
        $document = [System.Text.Json.JsonDocument]::Parse(
            [System.IO.File]::ReadAllText($Path, [System.Text.UTF8Encoding]::new($false, $true)),
            $options)
        try {
            if ($document.RootElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
                throw "$Description根节点必须是 JSON 对象。"
            }
            return $document.RootElement.Clone()
        }
        finally {
            $document.Dispose()
        }
    }
    catch {
        if ($_.Exception.Message -like "$Description*") { throw }
        throw "$Description不是有效 JSON：$($_.Exception.Message)"
    }
}

function Assert-ExactProperties(
    [System.Text.Json.JsonElement]$Value,
    [string[]]$Allowed,
    [string]$Description) {
    if ($Value.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
        throw "$Description必须是 JSON 对象。"
    }

    $actual = @($Value.EnumerateObject() | ForEach-Object Name)
    $unique = @($actual | Select-Object -Unique)
    $unknown = @($unique | Where-Object { $Allowed -cnotcontains $_ })
    $missing = @($Allowed | Where-Object { $unique -cnotcontains $_ })
    if ($actual.Count -ne $unique.Count -or $unknown.Count -gt 0 -or $missing.Count -gt 0) {
        throw "$Description字段不符合 schema v2。未知：$($unknown -join '、')；缺失：$($missing -join '、')。"
    }
}

function Get-RequiredProperty(
    [System.Text.Json.JsonElement]$Object,
    [string]$Name,
    [string]$Description) {
    $property = [System.Text.Json.JsonElement]::new()
    if (-not $Object.TryGetProperty($Name, [ref]$property)) {
        throw "$Description缺少字段 $Name。"
    }
    return $property
}

function Get-RequiredString(
    [System.Text.Json.JsonElement]$Object,
    [string]$Name,
    [string]$Description) {
    $value = Get-RequiredProperty $Object $Name $Description
    if ($value.ValueKind -ne [System.Text.Json.JsonValueKind]::String -or [string]::IsNullOrWhiteSpace($value.GetString())) {
        throw "$Description的 $Name 必须是非空字符串。"
    }
    return $value.GetString()
}

function Test-Identifier([string]$Value) {
    return $Value -cmatch '^[a-z0-9]+(?:[.-][a-z0-9]+)*$'
}

function Test-SemanticVersion([string]$Value) {
    return $Value -cmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-((?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$'
}

function Assert-StringArray(
    [System.Text.Json.JsonElement]$Value,
    [string]$Description) {
    if ($Value.ValueKind -ne [System.Text.Json.JsonValueKind]::Array) {
        throw "$Description必须是 JSON 字符串数组。"
    }

    $values = @($Value.EnumerateArray())
    if ($values | Where-Object { $_.ValueKind -ne [System.Text.Json.JsonValueKind]::String -or [string]::IsNullOrWhiteSpace($_.GetString()) }) {
        throw "$Description只能包含非空字符串。"
    }
}

function Assert-RelativeEntry([string]$Value, [string]$Description) {
    if ([string]::IsNullOrWhiteSpace($Value) -or [System.IO.Path]::IsPathRooted($Value) -or
        $Value -match '(^|[\\/])\.\.([\\/]|$)' -or $Value -match '(^|[\\/])\.?([\\/]|$)') {
        throw "$Description必须是扩展包内的相对路径。"
    }

    try {
        # 与宿主 ExtensionManifestParser 保持同一 Windows/.NET 路径归一化边界，防止 JSON 字符串绕过正则检查。
        $root = [System.IO.Path]::GetFullPath('.')
        $resolved = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($root, $Value))
        $relative = [System.IO.Path]::GetRelativePath($root, $resolved)
        if ([System.IO.Path]::IsPathRooted($relative) -or $relative -ceq '..' -or
            $relative.StartsWith("..$([System.IO.Path]::DirectorySeparatorChar)", [System.StringComparison]::Ordinal) -or
            $relative.StartsWith("..$([System.IO.Path]::AltDirectorySeparatorChar)", [System.StringComparison]::Ordinal)) {
            throw "$Description必须是扩展包内的相对路径。"
        }
    }
    catch {
        throw "$Description必须是扩展包内的相对路径。"
    }
}

function Assert-Manifest(
    [System.Text.Json.JsonElement]$Manifest,
    [string]$Description) {
    Assert-ExactProperties $Manifest @(
        'schemaVersion', 'id', 'name', 'version', 'kind', 'publisherId', 'hostApiVersion', 'minHostVersion',
        'runtime', 'capabilities', 'permissions', 'dependencies') $Description

    $schemaVersion = 0
    $schemaElement = Get-RequiredProperty $Manifest 'schemaVersion' $Description
    if ($schemaElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or
        -not $schemaElement.TryGetInt32([ref]$schemaVersion) -or $schemaVersion -ne 2) {
        throw "$Description schemaVersion 必须是整数 2。"
    }

    $id = Get-RequiredString $Manifest 'id' $Description
    $name = Get-RequiredString $Manifest 'name' $Description
    $version = Get-RequiredString $Manifest 'version' $Description
    $kind = Get-RequiredString $Manifest 'kind' $Description
    $publisherId = Get-RequiredString $Manifest 'publisherId' $Description
    $hostApiVersion = Get-RequiredString $Manifest 'hostApiVersion' $Description
    $minHostVersion = Get-RequiredString $Manifest 'minHostVersion' $Description
    if (-not (Test-Identifier $id) -or -not (Test-Identifier $publisherId)) {
        throw "$Description的 id 或 publisherId 无效。"
    }
    if (-not (Test-SemanticVersion $version) -or -not (Test-SemanticVersion $minHostVersion)) {
        throw "$Description的 version 或 minHostVersion 无效。"
    }
    if ($hostApiVersion -cne '1.0') {
        throw "$Description的 hostApiVersion 必须为 1.0。"
    }

    $runtime = Get-RequiredProperty $Manifest 'runtime' $Description
    if ($runtime.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
        throw "$Description的 runtime 必须是 JSON 对象。"
    }
    $runtimeKind = Get-RequiredString $runtime 'kind' "$Description runtime"
    $expectedRuntimeFields = switch ("$kind/$runtimeKind") {
        'workspace/web' { @('kind', 'entry') }
        'analysis/process' { @('kind', 'protocol', 'entry') }
        'analysis/content' { @('kind') }
        'maintenance/content' { @('kind') }
        default { throw "$Description的 kind/runtime 组合不受支持：$kind/$runtimeKind。" }
    }
    Assert-ExactProperties $runtime $expectedRuntimeFields "$Description runtime"
    if ($runtimeKind -in @('web', 'process')) {
        Assert-RelativeEntry (Get-RequiredString $runtime 'entry' "$Description runtime") "$Description runtime.entry"
    }
    if ($runtimeKind -ceq 'process' -and (Get-RequiredString $runtime 'protocol' "$Description runtime") -cne 'analysis-process-v1') {
        throw "$Description的 analysis/process runtime 必须使用 analysis-process-v1 协议。"
    }

    $capabilities = Get-RequiredProperty $Manifest 'capabilities' $Description
    $permissions = Get-RequiredProperty $Manifest 'permissions' $Description
    $dependencies = Get-RequiredProperty $Manifest 'dependencies' $Description
    Assert-StringArray $capabilities "$Description capabilities"
    Assert-StringArray $permissions "$Description permissions"
    if ($capabilities.GetArrayLength() -eq 0) {
        throw "$Description capabilities 不能为空。"
    }
    if ($kind -cne 'workspace' -and $permissions.GetArrayLength() -ne 0) {
        throw "$Description只有 workspace 扩展可以声明 permissions。"
    }

    $allowedCapabilities = switch ("$kind/$runtimeKind") {
        'workspace/web' { @('workspace.page') }
        'analysis/process' { @('analysis.engine', 'analysis.scope.comprehensive', 'analysis.scope.storage') }
        'analysis/content' { @('analysis.rule-pack', 'analysis.report-template') }
        'maintenance/content' { @('maintenance.workflow-pack', 'maintenance.command-profile') }
    }
    $capabilityValues = @($capabilities.EnumerateArray() | ForEach-Object GetString)
    if ($capabilityValues | Where-Object { $allowedCapabilities -cnotcontains $_ }) {
        throw "$Description声明了当前运行时不允许的 capability。"
    }
    if (($kind -ceq 'workspace' -and $capabilityValues -cnotcontains 'workspace.page') -or
        ($kind -ceq 'analysis' -and $runtimeKind -ceq 'process' -and $capabilityValues -cnotcontains 'analysis.engine')) {
        throw "$Description缺少当前运行时的必需 capability。"
    }

    if ($dependencies.ValueKind -ne [System.Text.Json.JsonValueKind]::Array) {
        throw "$Description dependencies 必须是 JSON 数组。"
    }
    $dependencyIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($dependency in $dependencies.EnumerateArray()) {
        Assert-ExactProperties $dependency @('id', 'version') "$Description dependencies 项"
        $dependencyId = Get-RequiredString $dependency 'id' "$Description dependencies 项"
        $dependencyVersion = Get-RequiredString $dependency 'version' "$Description dependencies 项"
        if (-not (Test-Identifier $dependencyId) -or -not (Test-SemanticVersion $dependencyVersion) -or
            $dependencyId -ceq $id -or -not $dependencyIds.Add($dependencyId)) {
            throw "$Description dependencies 包含无效、重复或自身依赖。"
        }
    }
}

function Test-HasOnlySafeAsciiUriCharacters([string]$Value) {
    $allowedPunctuation = "._~:/?@!$&'()*+,;=-"
    for ($index = 0; $index -lt $Value.Length; $index++) {
        $character = $Value[$index]
        if ([int][char]$character -gt 0x7f) {
            return $false
        }
        if ([char]::IsAsciiLetterOrDigit($character) -or $allowedPunctuation.Contains([string]$character)) {
            continue
        }
        if ($character -ne '%' -or $index + 2 -ge $Value.Length -or
            -not [System.Uri]::IsHexDigit($Value[$index + 1]) -or
            -not [System.Uri]::IsHexDigit($Value[$index + 2])) {
            return $false
        }
        $index += 2
    }
    return $true
}

function Test-StrictIpv4([string]$HostName) {
    $octets = $HostName.Split('.')
    if ($octets.Count -ne 4) {
        return $false
    }

    foreach ($octet in $octets) {
        $value = 0
        if ($octet.Length -eq 0 -or ($octet.Length -gt 1 -and $octet[0] -eq '0') -or
            -not [System.Text.RegularExpressions.Regex]::IsMatch($octet, '^[0-9]+$') -or
            -not [int]::TryParse($octet, [ref]$value) -or $value -gt 255) {
            return $false
        }
    }
    return $true
}

function Test-AsciiDnsHost([string]$HostName) {
    if (-not ($HostName.ToCharArray() | Where-Object { [char]::IsAsciiLetter($_) })) {
        return $false
    }

    return @($HostName.Split('.') | Where-Object {
        -not [System.Text.RegularExpressions.Regex]::IsMatch($_, '^[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?$')
    }).Count -eq 0
}

function Test-SafeHttpsReleaseUrl([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value) -or -not (Test-HasOnlySafeAsciiUriCharacters $Value)) {
        return $false
    }

    $uri = $null
    if (-not [System.Uri]::TryCreate($Value, [System.UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -cne 'https' -or -not [string]::IsNullOrEmpty($uri.UserInfo) -or
        -not [string]::IsNullOrEmpty($uri.Fragment) -or -not $uri.IsDefaultPort -or
        $uri.HostNameType -eq [System.UriHostNameType]::IPv6) {
        return $false
    }

    $schemeSeparator = $Value.IndexOf('://', [System.StringComparison]::Ordinal)
    if ($schemeSeparator -lt 0) {
        return $false
    }
    $authorityStart = $schemeSeparator + 3
    $authorityEnd = $Value.IndexOfAny([char[]]@([char]'/', [char]'?', [char]'#'), $authorityStart)
    $authority = if ($authorityEnd -lt 0) { $Value.Substring($authorityStart) } else { $Value.Substring($authorityStart, $authorityEnd - $authorityStart) }
    if ($authority.EndsWith(':443', [System.StringComparison]::Ordinal)) {
        $authority = $authority.Substring(0, $authority.Length - 4)
    }
    if ($authority.Length -eq 0 -or $authority.Length -gt 253 -or $authority.Contains(':') -or $authority.Contains('@')) {
        return $false
    }

    return (Test-StrictIpv4 $authority) -or (Test-AsciiDnsHost $authority)
}
function Assert-SafeAssetFileName([string]$File, [string]$Description) {
    $reserved = [System.Collections.Generic.HashSet[string]]::new(
        [string[]]@(
            'CON', 'PRN', 'AUX', 'NUL',
            'COM1', 'COM2', 'COM3', 'COM4', 'COM5', 'COM6', 'COM7', 'COM8', 'COM9',
            'LPT1', 'LPT2', 'LPT3', 'LPT4', 'LPT5', 'LPT6', 'LPT7', 'LPT8', 'LPT9',
            'COM¹', 'COM²', 'COM³', 'LPT¹', 'LPT²', 'LPT³'),
        [System.StringComparer]::OrdinalIgnoreCase)
    $deviceName = [System.IO.Path]::GetFileNameWithoutExtension($File).Split('.', 2)[0].TrimEnd(' ', '.')
    if ([string]::IsNullOrWhiteSpace($File) -or $File.Contains('/') -or $File.Contains('\') -or
        $File.Contains('..') -or [System.IO.Path]::IsPathRooted($File) -or
        $File -cne [System.IO.Path]::GetFileName($File) -or
        -not $File.EndsWith('.zip', [System.StringComparison]::OrdinalIgnoreCase) -or
        ($File.ToCharArray() | Where-Object { [int][char]$_ -lt 32 -or [System.IO.Path]::GetInvalidFileNameChars() -ccontains $_ }) -or
        $reserved.Contains($deviceName)) {
        throw "$Description必须是安全的 ZIP 文件名：$File。"
    }
}

function Assert-ReleaseMetadataPackage(
    [System.Text.Json.JsonElement]$Package,
    [string]$Description) {
    Assert-ExactProperties $Package @('manifest', 'file', 'url', 'size', 'sha256', 'keyId', 'signature') $Description

    $manifest = Get-RequiredProperty $Package 'manifest' $Description
    Assert-Manifest $manifest "$Description manifest"
    $file = Get-RequiredString $Package 'file' $Description
    Assert-SafeAssetFileName $file "$Description file"
    $url = Get-RequiredString $Package 'url' $Description
    $uri = $null
    if (-not (Test-SafeHttpsReleaseUrl $url) -or
        -not [System.Uri]::TryCreate($url, [System.UriKind]::Absolute, [ref]$uri) -or
        [System.Uri]::UnescapeDataString([System.IO.Path]::GetFileName($uri.AbsolutePath)) -cne $file) {
        throw "$Description url 必须是明确指向同名 ZIP 资产的 HTTPS 地址。"
    }

    $size = 0L
    $sizeElement = Get-RequiredProperty $Package 'size' $Description
    if ($sizeElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or
        -not $sizeElement.TryGetInt64([ref]$size) -or $size -lt 1 -or $size -gt $maximumPackageBytes) {
        throw "$Description size 必须是 1 到 $maximumPackageBytes 的 JSON 整数。"
    }
    $sha256 = Get-RequiredString $Package 'sha256' $Description
    if ($sha256 -cnotmatch '^[0-9a-fA-F]{64}$') {
        throw "$Description sha256 必须是 64 位十六进制 SHA-256。"
    }
    $keyId = Get-RequiredString $Package 'keyId' $Description
    $signature = Get-RequiredString $Package 'signature' $Description
    try {
        if ([System.Convert]::FromBase64String($signature).Length -ne 64) {
            throw "$Description signature 必须是 64 字节 Ed25519 签名。"
        }
    }
    catch [System.FormatException] {
        throw "$Description signature 必须是有效 Base64 编码。"
    }

    return [pscustomobject]@{
        Manifest = $manifest
        File = $file
        Url = $url
        Size = $size
        Sha256 = $sha256
        KeyId = $keyId
        Signature = $signature
    }
}

function ConvertTo-CanonicalJson([System.Text.Json.JsonElement]$Value) {
    switch ($Value.ValueKind) {
        ([System.Text.Json.JsonValueKind]::Object) {
            $properties = @($Value.EnumerateObject() | Sort-Object -Property Name)
            return '{' + (($properties | ForEach-Object {
                [System.Text.Json.JsonSerializer]::Serialize[string]($_.Name) + ':' + (ConvertTo-CanonicalJson $_.Value)
            }) -join ',') + '}'
        }
        ([System.Text.Json.JsonValueKind]::Array) {
            return '[' + (($Value.EnumerateArray() | ForEach-Object { ConvertTo-CanonicalJson $_ }) -join ',') + ']'
        }
        ([System.Text.Json.JsonValueKind]::String) {
            return [System.Text.Json.JsonSerializer]::Serialize[string]($Value.GetString())
        }
        default {
            return $Value.GetRawText()
        }
    }
}

function Read-ZipManifest([string]$Path) {
    try {
        Add-Type -AssemblyName System.IO.Compression
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
        try {
            $entries = @($archive.Entries | Where-Object { $_.FullName -ceq 'manifest.json' })
            if ($entries.Count -ne 1) {
                throw "最终 ZIP 必须且只能包含一个根级 manifest.json；实际数量：$($entries.Count)。"
            }
            $entry = $entries[0]
            $stream = $entry.Open()
            try {
                $reader = [System.IO.StreamReader]::new($stream, [System.Text.UTF8Encoding]::new($false, $true), $true)
                try {
                    $json = $reader.ReadToEnd()
                }
                finally {
                    $reader.Dispose()
                }
            }
            finally {
                $stream.Dispose()
            }
            $options = [System.Text.Json.JsonDocumentOptions]::new()
            $options.AllowTrailingCommas = $false
            $options.CommentHandling = [System.Text.Json.JsonCommentHandling]::Disallow
            $document = [System.Text.Json.JsonDocument]::Parse($json, $options)
            try {
                if ($document.RootElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
                    throw '最终 ZIP 的根级 manifest.json 必须是 JSON 对象。'
                }
                return $document.RootElement.Clone()
            }
            finally {
                $document.Dispose()
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    catch {
        if ($_.Exception.Message -like '最终 ZIP*') { throw }
        throw "无法读取最终 ZIP 或根级 manifest.json：$($_.Exception.Message)"
    }
}

# metadata 只负责跨仓交接；本脚本不读取 Catalog，不接受任何公钥或 trust scope。
$resolvedMetadataPath = Assert-InputFile $ReleaseMetadataPath 'release-metadata.json'
$resolvedPackagePath = Assert-InputFile $PackagePath '最终 ZIP'
if (-not [string]::Equals([System.IO.Path]::GetExtension($resolvedPackagePath), '.zip', [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'PackagePath 必须指向普通 .zip 文件。'
}
if ([string]::IsNullOrWhiteSpace($ExtensionId) -or -not (Test-Identifier $ExtensionId)) {
    throw 'ExtensionId 必须是明确的小写扩展 ID。'
}
$description = $ReviewedDescription.Trim()
if ([string]::IsNullOrWhiteSpace($description)) {
    throw 'ReviewedDescription 去除首尾空白后不能为空；不得从 metadata、Catalog 或扩展名称推测描述。'
}

$metadata = Read-JsonObject $resolvedMetadataPath 'release-metadata.json'
Assert-ExactProperties $metadata @('schemaVersion', 'generatedAtUtc', 'packages') 'release-metadata.json'
$schemaVersion = 0
$schemaElement = Get-RequiredProperty $metadata 'schemaVersion' 'release-metadata.json'
if ($schemaElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or
    -not $schemaElement.TryGetInt32([ref]$schemaVersion) -or $schemaVersion -ne 2) {
    throw 'release-metadata.json schemaVersion 必须是整数 2。'
}
$generatedAtUtc = Get-RequiredString $metadata 'generatedAtUtc' 'release-metadata.json'
$parsedGeneratedAt = [System.DateTimeOffset]::MinValue
if (-not [System.DateTimeOffset]::TryParse($generatedAtUtc, [ref]$parsedGeneratedAt) -or
    $parsedGeneratedAt.Offset -ne [System.TimeSpan]::Zero) {
    throw 'release-metadata.json generatedAtUtc 必须是 UTC 时间。'
}
$packageElements = Get-RequiredProperty $metadata 'packages' 'release-metadata.json'
if ($packageElements.ValueKind -ne [System.Text.Json.JsonValueKind]::Array -or $packageElements.GetArrayLength() -eq 0) {
    throw 'release-metadata.json packages 必须是非空 JSON 数组。'
}

$packages = @(foreach ($packageElement in $packageElements.EnumerateArray()) {
    Assert-ReleaseMetadataPackage $packageElement 'release-metadata.json packages 项'
})
$selected = @($packages | Where-Object { $_.Manifest.GetProperty('id').GetString() -ceq $ExtensionId })
if ($selected.Count -eq 0) {
    throw "release-metadata.json 中未找到 ExtensionId：$ExtensionId。"
}
if ($selected.Count -ne 1) {
    throw "release-metadata.json 中存在重复 ExtensionId：$ExtensionId。"
}
$selectedPackage = $selected[0]

$zipManifest = Read-ZipManifest $resolvedPackagePath
Assert-Manifest $zipManifest '最终 ZIP manifest.json'
if ((ConvertTo-CanonicalJson $zipManifest) -cne (ConvertTo-CanonicalJson $selectedPackage.Manifest)) {
    throw '最终 ZIP manifest 与选中的 metadata package.manifest 不完整语义一致；拒绝物化 Bundled Extension 锁定清单。'
}

$actualFileName = [System.IO.Path]::GetFileName($resolvedPackagePath)
if ($selectedPackage.File -cne $actualFileName) {
    throw "metadata package.file 必须与最终 ZIP 文件名精确匹配：metadata 为 $($selectedPackage.File)，ZIP 为 $actualFileName。"
}
$actualSize = (Get-Item -LiteralPath $resolvedPackagePath).Length
if ($actualSize -ne $selectedPackage.Size) {
    throw "最终 ZIP size 校验失败：实际 $actualSize，metadata 为 $($selectedPackage.Size)。"
}
$actualSha256 = (Get-FileHash -LiteralPath $resolvedPackagePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualSha256 -cne $selectedPackage.Sha256.ToLowerInvariant()) {
    throw "最终 ZIP SHA-256 校验失败：实际 $actualSha256，metadata 为 $($selectedPackage.Sha256)。"
}

# 身份字段始终从已核对的 ZIP manifest 取值；metadata 只保留版本 URL、原始 ZIP 大小、哈希和签名交接值。
$bundle = [ordered]@{
    schemaVersion = 2
    extensions = @(
        [ordered]@{
            id = $zipManifest.GetProperty('id').GetString()
            name = $zipManifest.GetProperty('name').GetString()
            description = $description
            publisherId = $zipManifest.GetProperty('publisherId').GetString()
            kind = $zipManifest.GetProperty('kind').GetString()
            asset = $actualFileName
            release = [ordered]@{
                version = $zipManifest.GetProperty('version').GetString()
                minHostVersion = $zipManifest.GetProperty('minHostVersion').GetString()
                url = $selectedPackage.Url
                size = $actualSize
                sha256 = $actualSha256
                signature = [ordered]@{
                    keyId = $selectedPackage.KeyId
                    signature = $selectedPackage.Signature
                }
            }
        }
    )
}

$resolvedOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [System.IO.Path]::GetDirectoryName($resolvedOutputPath)
if ([string]::IsNullOrWhiteSpace($outputDirectory)) {
    throw "输出路径无效：$resolvedOutputPath"
}
[System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
if (Test-Path -LiteralPath $resolvedOutputPath) {
    $existingOutput = Get-Item -LiteralPath $resolvedOutputPath
    if ($existingOutput.Attributes.HasFlag([System.IO.FileAttributes]::ReparsePoint)) {
        throw "输出 bundled-extensions.json 不能覆盖重解析点：$resolvedOutputPath"
    }
}

$tempOutputPath = Join-Path $outputDirectory (".bundled-extensions-" + [System.Guid]::NewGuid().ToString('N') + '.tmp')
try {
    [System.IO.File]::WriteAllText(
        $tempOutputPath,
        ($bundle | ConvertTo-Json -Depth 8),
        [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::Move($tempOutputPath, $resolvedOutputPath, $true)
}
finally {
    if (Test-Path -LiteralPath $tempOutputPath) {
        [System.IO.File]::Delete($tempOutputPath)
    }
}

Write-Host "已物化 Bundled Extension 锁定清单：$resolvedOutputPath（$ExtensionId $($zipManifest.GetProperty('version').GetString())）。"
