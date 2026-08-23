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
    Task<ExtensionPackageVerificationResult> VerifyAsync(
        ExtensionPackageVerificationRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 入口首先复制原始 ZIP，后续校验只使用不可变快照。
/// 在解析 ZIP 前依次完成大小、SHA-256、宿主信任范围和 Ed25519 原始字节验签；不存在 unsigned 或 developer mode 分支。
/// </summary>
public sealed class ExtensionPackageVerifier : IExtensionPackageVerifier
{
    private const int MaximumManifestBytes = 1024 * 1024;
    private readonly IExtensionTrustStore _trustStore;

    public ExtensionPackageVerifier(IExtensionTrustStore trustStore)
    {
        _trustStore = trustStore ?? throw new ArgumentNullException(nameof(trustStore));
    }

    public async Task<ExtensionPackageVerificationResult> VerifyAsync(
        ExtensionPackageVerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
            throw new InvalidDataException("扩展包验签请求不能为空。");
        if (request.PackageBytes is null || request.CatalogItem is null || request.Release is null)
            throw new InvalidDataException("扩展包验签请求缺少 ZIP、Catalog 条目或发布信息。");

        // 请求中的 byte[] 可由调用方继续持有；必须在第一次校验前复制，消除检查与使用之间的竞态。
        var packageBytes = request.PackageBytes.ToArray();
        var release = request.Release;
        var catalogItem = request.CatalogItem;
        cancellationToken.ThrowIfCancellationRequested();

        if (packageBytes.LongLength != release.Size)
            throw new InvalidDataException($"扩展包大小不一致：Catalog 为 {release.Size} 字节，实际为 {packageBytes.LongLength} 字节。");

        var packageSha256 = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant();
        if (!string.Equals(packageSha256, release.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("扩展包 SHA-256 校验失败，下载内容可能已损坏或被篡改。");

        if (release.Signature is null || string.IsNullOrWhiteSpace(release.Signature.KeyId))
            throw new InvalidDataException("扩展包缺少 Ed25519 签名或签名 keyId，不允许以未签名模式加载。");
        if (!_trustStore.TryGetTrustedKey(release.Signature.KeyId, out var trustedKey))
            throw new InvalidDataException($"扩展包签名密钥不受信任：{release.Signature.KeyId}。");
        if (!string.Equals(catalogItem.PublisherId, trustedKey.PublisherId, StringComparison.Ordinal))
            throw new InvalidDataException("扩展 Catalog 的发布者与受信任密钥不一致。");
        if (!trustedKey.Scope.AllowedKinds.Contains(catalogItem.Kind))
            throw new InvalidDataException($"受信任密钥 {trustedKey.KeyId} 无权发布 {catalogItem.Kind} 类别扩展。");

        VerifyRawZipSignature(packageBytes, release.Signature.Signature, trustedKey);

        var manifest = await ReadManifestAsync(packageBytes, cancellationToken);
        ValidateManifestBinding(manifest, catalogItem, release, trustedKey);
        ValidatePermissions(manifest, trustedKey);

        return new ExtensionPackageVerificationResult
        {
            Manifest = manifest,
            TrustedKeyId = trustedKey.KeyId,
            PackageSha256 = packageSha256
        };
    }

    private static async Task<ExtensionManifest> ReadManifestAsync(
        byte[] packageBytes,
        CancellationToken cancellationToken)
    {
        using var package = new MemoryStream(packageBytes, writable: false);
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: false);
            _ = archive.Entries.Count;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            throw CorruptZip(exception);
        }

        using (archive)
        {
            var manifests = archive.Entries
                .Where(entry => string.Equals(entry.FullName, "manifest.json", StringComparison.Ordinal))
                .ToArray();
            if (manifests.Length != 1 || string.IsNullOrEmpty(manifests[0].Name))
                throw new InvalidDataException("扩展 ZIP 根目录必须且只能包含一个 manifest.json。");

            Stream manifestStream;
            try
            {
                manifestStream = manifests[0].Open();
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException)
            {
                throw CorruptZip(exception);
            }

            await using (manifestStream)
            {
                var json = await ReadBoundedUtf8Async(manifestStream, cancellationToken);
                var virtualDirectory = Path.Combine(Path.GetTempPath(), "HephaestusWorkbench", "VerifiedExtension");
                try
                {
                    return ExtensionManifestParser.Parse(json, virtualDirectory);
                }
                catch (ExtensionContractException exception)
                {
                    throw new InvalidDataException($"扩展包内 manifest.json 无效：{exception.Message}", exception);
                }
            }
        }
    }

    private static async Task<string> ReadBoundedUtf8Async(Stream stream, CancellationToken cancellationToken)
    {
        using var content = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException)
            {
                throw CorruptZip(exception);
            }
            catch (IOException exception)
            {
                throw CorruptZip(exception);
            }

            if (read == 0) break;
            if (content.Length + read > MaximumManifestBytes)
                throw new InvalidDataException("扩展 manifest.json 超过 1 MB 安全限制。");
            content.Write(buffer, 0, read);
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(content.GetBuffer(), 0, checked((int)content.Length));
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("扩展包内 manifest.json 不是有效的 UTF-8 文本。", exception);
        }
    }

    private static void ValidateManifestBinding(
        ExtensionManifest manifest,
        ExtensionCatalogItem catalogItem,
        ExtensionRelease release,
        TrustedPublisherKey trustedKey)
    {
        if (!string.Equals(manifest.Id, catalogItem.Id, StringComparison.Ordinal))
            throw new InvalidDataException("扩展 manifest 的 id 与 Catalog 不一致。");
        if (!string.Equals(manifest.Version, release.Version, StringComparison.Ordinal))
            throw new InvalidDataException("扩展 manifest 的 version 与 Catalog release 不一致。");
        if (!string.Equals(manifest.MinHostVersion, release.MinHostVersion, StringComparison.Ordinal))
            throw new InvalidDataException("扩展 manifest 的 minHostVersion 与 Catalog release 不一致。");
        if (!string.Equals(manifest.PublisherId, catalogItem.PublisherId, StringComparison.Ordinal) ||
            !string.Equals(manifest.PublisherId, trustedKey.PublisherId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("扩展 manifest、Catalog 与受信任密钥的发布者不一致。");
        }
        if (manifest.Kind != catalogItem.Kind)
            throw new InvalidDataException("扩展 manifest 声明的类别与 Catalog 不一致。");
    }

    private static void ValidatePermissions(ExtensionManifest manifest, TrustedPublisherKey trustedKey)
    {
        if (manifest.Permissions is null)
            throw new InvalidDataException("扩展 manifest 的权限列表不能为空。");

        var allowedPermissions = new HashSet<string>(trustedKey.Scope.Permissions, StringComparer.Ordinal);
        foreach (var permission in manifest.Permissions)
        {
            if (string.IsNullOrWhiteSpace(permission))
                throw new InvalidDataException("扩展 manifest 的权限项不能为空。");
            if (!allowedPermissions.Contains(permission))
                throw new InvalidDataException($"扩展 manifest 请求了受信任范围之外的权限：{permission}。");
        }
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

    private static InvalidDataException CorruptZip(Exception exception)
        => new("扩展 ZIP 已损坏或格式无效。", exception);
}
