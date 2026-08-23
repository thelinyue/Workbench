using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using HephaestusWorkbench.PluginSDK;
using NSec.Cryptography;

namespace HephaestusWorkbench.Services;

/// <summary>扩展包验签服务的可序列化请求；公钥只能由 IExtensionTrustStore 提供。</summary>
public sealed class ExtensionPackageVerificationRequest
{
    [JsonPropertyName("packageBytes")]
    public required byte[] PackageBytes { get; init; }

    [JsonPropertyName("catalogItem")]
    public required ExtensionCatalogItem CatalogItem { get; init; }

    [JsonPropertyName("release")]
    public required ExtensionRelease Release { get; init; }
}

/// <summary>扩展包通过完整性、信任范围和签名校验后的最小结果。</summary>
public sealed class ExtensionPackageVerificationResult
{
    [JsonPropertyName("manifest")]
    public required ExtensionManifest Manifest { get; init; }

    [JsonPropertyName("trustedKeyId")]
    public required string TrustedKeyId { get; init; }

    [JsonPropertyName("packageSha256")]
    public required string PackageSha256 { get; init; }
}

/// <summary>验证扩展 Catalog 发布信息、包内 manifest 和原始 ZIP 字节签名。</summary>
public interface IExtensionPackageVerifier
{
    ExtensionPackageVerificationResult Verify(ExtensionPackageVerificationRequest request);
}

/// <summary>
/// 按固定安全顺序验证扩展包：大小、SHA-256、宿主信任、发布者、类别、manifest 权限，最后验签原始 ZIP 字节。
/// 该实现没有 unsigned 或 developer mode 分支，任何缺失或无效签名都会拒绝。
/// </summary>
public sealed class ExtensionPackageVerifier : IExtensionPackageVerifier
{
    private const long MaximumManifestBytes = 1024 * 1024;
    private readonly IExtensionTrustStore _trustStore;

    public ExtensionPackageVerifier(IExtensionTrustStore trustStore)
    {
        _trustStore = trustStore ?? throw new ArgumentNullException(nameof(trustStore));
    }

    public ExtensionPackageVerificationResult Verify(ExtensionPackageVerificationRequest request)
    {
        if (request is null)
            throw new InvalidDataException("扩展包验签请求不能为空。");
        if (request.PackageBytes is null || request.CatalogItem is null || request.Release is null)
            throw new InvalidDataException("扩展包验签请求缺少 ZIP、Catalog 条目或发布信息。");

        var packageBytes = request.PackageBytes;
        var release = request.Release;
        var catalogItem = request.CatalogItem;

        if (packageBytes.LongLength != release.Size)
            throw new InvalidDataException($"扩展包大小不一致：Catalog 为 {release.Size} 字节，实际为 {packageBytes.LongLength} 字节。");

        var packageSha256 = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant();
        if (!string.Equals(packageSha256, release.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("扩展包 SHA-256 校验失败，下载内容可能已损坏或被篡改。");

        if (release.Signature is null || string.IsNullOrWhiteSpace(release.Signature.KeyId))
            throw new InvalidDataException("扩展包缺少 Ed25519 签名或签名 keyId，不允许以未签名模式加载。");
        if (!_trustStore.TryGetTrustedKey(release.Signature.KeyId, out var trustedKey))
            throw new InvalidDataException($"扩展包签名密钥不受信任：{release.Signature.KeyId}。");

        var manifest = ReadManifest(packageBytes);
        ValidatePublishers(manifest, catalogItem, trustedKey);
        ValidateKinds(manifest, catalogItem, trustedKey);
        ValidatePermissions(manifest, trustedKey);
        VerifyRawZipSignature(packageBytes, release.Signature.Signature, trustedKey);

        return new ExtensionPackageVerificationResult
        {
            Manifest = manifest,
            TrustedKeyId = trustedKey.KeyId,
            PackageSha256 = packageSha256
        };
    }

    private static ExtensionManifest ReadManifest(byte[] packageBytes)
    {
        try
        {
            using var package = new MemoryStream(packageBytes, writable: false);
            using var archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: false);
            var manifests = archive.Entries
                .Where(entry => string.Equals(entry.FullName, "manifest.json", StringComparison.Ordinal))
                .ToArray();
            if (manifests.Length != 1 || string.IsNullOrEmpty(manifests[0].Name))
                throw new InvalidDataException("扩展 ZIP 根目录必须且只能包含一个 manifest.json。");
            if (manifests[0].Length > MaximumManifestBytes)
                throw new InvalidDataException("扩展 manifest.json 超过 1 MB 安全限制。");

            using var reader = new StreamReader(manifests[0].Open(), new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            var json = reader.ReadToEnd();
            var virtualDirectory = Path.Combine(Path.GetTempPath(), "HephaestusWorkbench", "VerifiedExtension");
            return ExtensionManifestParser.Parse(json, virtualDirectory);
        }
        catch (ExtensionContractException exception)
        {
            throw new InvalidDataException($"扩展包内 manifest.json 无效：{exception.Message}", exception);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("扩展包内 manifest.json 不是有效的 UTF-8 文本。", exception);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"扩展 ZIP 无法读取：{exception.Message}", exception);
        }
    }

    private static void ValidatePublishers(
        ExtensionManifest manifest,
        ExtensionCatalogItem catalogItem,
        TrustedPublisherKey trustedKey)
    {
        if (!string.Equals(catalogItem.PublisherId, trustedKey.PublisherId, StringComparison.Ordinal) ||
            !string.Equals(manifest.PublisherId, trustedKey.PublisherId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("扩展 manifest、Catalog 与受信任密钥的发布者不一致。");
        }
    }

    private static void ValidateKinds(
        ExtensionManifest manifest,
        ExtensionCatalogItem catalogItem,
        TrustedPublisherKey trustedKey)
    {
        if (!trustedKey.Scope.AllowedKinds.Contains(catalogItem.Kind))
            throw new InvalidDataException($"受信任密钥 {trustedKey.KeyId} 无权发布 {catalogItem.Kind} 类别扩展。");
        if (manifest.Kind != catalogItem.Kind)
            throw new InvalidDataException("扩展 manifest 声明的类别与 Catalog 不一致。");
    }

    private static void ValidatePermissions(ExtensionManifest manifest, TrustedPublisherKey trustedKey)
    {
        var allowedPermissions = new HashSet<string>(trustedKey.Scope.Permissions, StringComparer.Ordinal);
        var deniedPermission = manifest.Permissions.FirstOrDefault(permission => !allowedPermissions.Contains(permission));
        if (deniedPermission is not null)
            throw new InvalidDataException($"扩展 manifest 请求了受信任范围之外的权限：{deniedPermission}。");
    }

    private static void VerifyRawZipSignature(
        byte[] packageBytes,
        string? signatureBase64,
        TrustedPublisherKey trustedKey)
    {
        byte[] signature;
        byte[] publicKeyBytes;
        try
        {
            signature = Convert.FromBase64String(signatureBase64 ?? string.Empty);
            publicKeyBytes = Convert.FromBase64String(trustedKey.PublicKeyBase64);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("扩展包签名或受信任公钥不是有效的 Base64。", exception);
        }

        if (signature.Length != 64)
            throw new InvalidDataException("扩展包 Ed25519 签名必须为 64 字节。");
        if (publicKeyBytes.Length != 32)
            throw new InvalidDataException("受信任的 Ed25519 公钥必须为 32 字节。");

        try
        {
            var publicKey = PublicKey.Import(SignatureAlgorithm.Ed25519, publicKeyBytes, KeyBlobFormat.RawPublicKey);
            if (!SignatureAlgorithm.Ed25519.Verify(publicKey, packageBytes, signature))
                throw new InvalidDataException("扩展包 Ed25519 验签失败，原始 ZIP 字节可能已被篡改。");
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException("扩展包 Ed25519 公钥或签名格式无效。", exception);
        }
    }
}
