using System.Text.Json;
using System.Text.Json.Serialization;

namespace HephaestusWorkbench.PluginSDK;

/// <summary>扩展包的 Ed25519 签名信息；签名对象对应原始 ZIP 字节。</summary>
public sealed class ExtensionPackageSignature
{
    [JsonPropertyName("keyId")]
    public required string KeyId { get; init; }

    [JsonPropertyName("signature")]
    public required string Signature { get; init; }
}

/// <summary>目录中的一个不可变扩展发布版本。</summary>
public sealed class ExtensionRelease
{
    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("minHostVersion")]
    public required string MinHostVersion { get; init; }

    [JsonPropertyName("url")]
    public required string Url { get; init; }

    [JsonPropertyName("size")]
    public long Size { get; init; }

    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }

    [JsonPropertyName("signature")]
    public required ExtensionPackageSignature Signature { get; init; }
}

/// <summary>扩展目录条目；信任来源仍由宿主内置信任表决定，目录不能自行授予信任。</summary>
public sealed class ExtensionCatalogItem
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("publisherId")]
    public required string PublisherId { get; init; }

    [JsonPropertyName("kind")]
    public required ExtensionKind Kind { get; init; }

    [JsonPropertyName("releases")]
    public required IReadOnlyList<ExtensionRelease> Releases { get; init; } = Array.Empty<ExtensionRelease>();
}

/// <summary>在线或离线扩展目录 v2 根对象。</summary>
public sealed class ExtensionCatalogDocument
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("extensions")]
    public required IReadOnlyList<ExtensionCatalogItem> Extensions { get; init; } = Array.Empty<ExtensionCatalogItem>();
}

/// <summary>按严格 v2 JSON 结构读取扩展目录，未知字段会被拒绝。</summary>
public static class ExtensionCatalogParser
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static ExtensionCatalogDocument Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ExtensionContractException("扩展目录内容不能为空。");

        ExtensionCatalogDocument catalog;
        try
        {
            catalog = JsonSerializer.Deserialize<ExtensionCatalogDocument>(json, SerializerOptions)
                ?? throw new JsonException("扩展目录内容为空。");
        }
        catch (JsonException exception)
        {
            throw new ExtensionContractException($"扩展目录 JSON 不符合 v2 结构：{exception.Message}");
        }

        if (catalog.SchemaVersion != 2)
            throw new ExtensionContractException("扩展目录 schemaVersion 必须为 2。");

        ValidateCatalog(catalog);
        return catalog;
    }

    private static void ValidateCatalog(ExtensionCatalogDocument catalog)
    {
        if (catalog.Extensions is null)
            throw new ExtensionContractException("扩展目录 extensions 不能为空。");

        var extensionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var extension in catalog.Extensions)
        {
            if (extension is null)
                throw new ExtensionContractException("扩展目录 extensions 不能包含 null 元素。");
            if (!ExtensionContractValues.IsIdentifier(extension.Id))
                throw new ExtensionContractException("扩展目录中的 id 无效。");
            if (!ExtensionContractValues.IsIdentifier(extension.PublisherId))
                throw new ExtensionContractException($"扩展 {extension.Id} 的 publisherId 无效。");
            if (string.IsNullOrWhiteSpace(extension.Name) || string.IsNullOrWhiteSpace(extension.Description))
                throw new ExtensionContractException($"扩展 {extension.Id} 的 name 和 description 不能为空。");
            if (!extensionIds.Add(extension.Id))
                throw new ExtensionContractException($"扩展目录包含重复扩展 {extension.Id}。");
            if (extension.Releases is null || extension.Releases.Count == 0)
                throw new ExtensionContractException($"扩展 {extension.Id} 的 releases 不能为空。");

            var versions = new HashSet<string>(StringComparer.Ordinal);
            foreach (var release in extension.Releases)
            {
                if (release is null)
                    throw new ExtensionContractException($"扩展 {extension.Id} 的 releases 不能包含 null 元素。");
                ValidateRelease(extension.Id, release);
                if (!versions.Add(release.Version))
                    throw new ExtensionContractException($"扩展 {extension.Id} 包含重复版本 {release.Version}。");
            }
        }
    }

    private static void ValidateRelease(string extensionId, ExtensionRelease release)
    {
        if (!ExtensionContractValues.IsSemanticVersion(release.Version))
            throw new ExtensionContractException($"扩展 {extensionId} 的 release version 无效。");
        if (!ExtensionContractValues.IsSemanticVersion(release.MinHostVersion))
            throw new ExtensionContractException($"扩展 {extensionId} 的 minHostVersion 无效。");
        if (!Uri.TryCreate(release.Url, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ExtensionContractException($"扩展 {extensionId} 的发布地址必须使用 HTTPS。");
        }
        if (release.Size <= 0)
            throw new ExtensionContractException($"扩展 {extensionId} 的发布包大小必须大于 0。");
        if (!ExtensionContractValues.IsSha256(release.Sha256))
            throw new ExtensionContractException($"扩展 {extensionId} 的 SHA-256 无效。");
        if (release.Signature is null)
            throw new ExtensionContractException($"扩展 {extensionId} 缺少 Ed25519 签名信息。");
        if (string.IsNullOrWhiteSpace(release.Signature.KeyId))
            throw new ExtensionContractException($"扩展 {extensionId} 缺少签名 keyId。");
        if (string.IsNullOrWhiteSpace(release.Signature.Signature))
            throw new ExtensionContractException($"扩展 {extensionId} 缺少 Ed25519 签名。");

        try
        {
            if (Convert.FromBase64String(release.Signature.Signature).Length != 64)
                throw new ExtensionContractException($"扩展 {extensionId} 的 Ed25519 签名必须为 64 字节。");
        }
        catch (FormatException)
        {
            throw new ExtensionContractException($"扩展 {extensionId} 的 Ed25519 签名不是有效的 Base64。");
        }
    }
}
