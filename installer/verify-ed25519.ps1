param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,
    [Parameter(Mandatory = $true)]
    [string]$PublicKeyBase64,
    [Parameter(Mandatory = $true)]
    [string]$SignatureBase64,
    [string]$ProjectAssetsPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src\HephaestusWorkbench.Services\obj\project.assets.json')
)

$ErrorActionPreference = 'Stop'

function Convert-RequiredBase64([string]$Value, [int]$ExpectedLength, [string]$Description) {
    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw "$Description 不能为空。"
    }
    try {
        $bytes = [Convert]::FromBase64String($Value)
    }
    catch {
        throw "$Description 不是有效 Base64。"
    }
    if ($bytes.Length -ne $ExpectedLength) {
        throw "$Description 必须解码为 $ExpectedLength 字节。"
    }
    return $bytes
}

# 发布预检直接调用宿主 NSec 所依赖的同一 libsodium Ed25519 原语，签名覆盖下载后的原始 ZIP 字节。
# 公钥只能由 build-installer.ps1 已解析的正式 Trust Store 传入，本脚本不从 Catalog 或网络读取密钥。
$resolvedPackagePath = [IO.Path]::GetFullPath($PackagePath)
if (-not (Test-Path -LiteralPath $resolvedPackagePath -PathType Leaf)) {
    throw "待验签的 Bundled Extension ZIP 不存在：$resolvedPackagePath"
}
$resolvedAssetsPath = [IO.Path]::GetFullPath($ProjectAssetsPath)
if (-not (Test-Path -LiteralPath $resolvedAssetsPath -PathType Leaf)) {
    throw "缺少 NuGet 资产清单 project.assets.json，请先执行 dotnet restore：$resolvedAssetsPath"
}

try {
    $assets = Get-Content -LiteralPath $resolvedAssetsPath -Raw -Encoding UTF8 | ConvertFrom-Json -AsHashtable
}
catch {
    throw "NuGet 资产清单 project.assets.json 无法解析：$($_.Exception.Message)"
}
$packageFolders = @($assets.packageFolders.Keys)
$libsodiumLibraries = @($assets.libraries.Keys | Where-Object { $_ -like 'libsodium/*' })
if ($packageFolders.Count -ne 1 -or $libsodiumLibraries.Count -ne 1) {
    throw 'NuGet 资产清单无法唯一解析宿主使用的 libsodium 依赖。'
}
if ([Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne [Runtime.InteropServices.Architecture]::X64) {
    throw 'Bundled Extension Ed25519 发布预检只支持 Windows x64 PowerShell。'
}

$packageFolder = [string]$packageFolders[0]
$libsodiumLibrary = $assets.libraries[$libsodiumLibraries[0]]
$libsodiumPath = Join-Path $packageFolder (Join-Path ([string]$libsodiumLibrary.path) 'runtimes\win-x64\native\libsodium.dll')
if (-not (Test-Path -LiteralPath $libsodiumPath -PathType Leaf)) {
    throw "未找到宿主锁定的 libsodium.dll：$libsodiumPath"
}

$publicKey = Convert-RequiredBase64 $PublicKeyBase64 32 'Ed25519 公钥'
$signature = Convert-RequiredBase64 $SignatureBase64 64 'Ed25519 签名'
$packageBytes = [IO.File]::ReadAllBytes($resolvedPackagePath)
$nativeHandle = [Runtime.InteropServices.NativeLibrary]::Load($libsodiumPath)
try {
    if (-not ('HephaestusWorkbench.Release.Ed25519Verifier' -as [type])) {
        Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

namespace HephaestusWorkbench.Release
{
    public static class Ed25519Verifier
    {
        [DllImport("libsodium", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sodium_init();

        [DllImport("libsodium", CallingConvention = CallingConvention.Cdecl)]
        private static extern int crypto_sign_verify_detached(
            byte[] signature,
            byte[] message,
            ulong messageLength,
            byte[] publicKey);

        public static bool Verify(byte[] packageBytes, byte[] publicKey, byte[] signature)
        {
            if (packageBytes == null || publicKey == null || signature == null)
                return false;
            if (publicKey.Length != 32 || signature.Length != 64 || sodium_init() < 0)
                return false;
            return crypto_sign_verify_detached(signature, packageBytes, (ulong)packageBytes.LongLength, publicKey) == 0;
        }
    }
}
"@
    }

    if (-not [HephaestusWorkbench.Release.Ed25519Verifier]::Verify($packageBytes, $publicKey, $signature)) {
        throw "Bundled Extension Ed25519 验签失败，原始 ZIP 字节或签名可能已被篡改：$resolvedPackagePath"
    }
}
finally {
    [Runtime.InteropServices.NativeLibrary]::Free($nativeHandle)
}

Write-Host "Bundled Extension Ed25519 验签通过：$resolvedPackagePath"
