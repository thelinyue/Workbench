using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HephaestusWorkbench.PluginSDK;

/// <summary>
/// 一份正式扩展发布包的跨仓机器交接记录。manifest 来自最终 ZIP，metadata 本身不携带公钥，也不建立发布者信任。
/// </summary>
public sealed class ExtensionReleaseMetadataPackage
{
    [JsonPropertyName("manifest")]
    public required ExtensionManifest Manifest { get; init; }

    [JsonPropertyName("file")]
    public required string File { get; init; }

    [JsonPropertyName("url")]
    public required string Url { get; init; }

    [JsonPropertyName("size")]
    public long Size { get; init; }

    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }

    [JsonPropertyName("keyId")]
    public required string KeyId { get; init; }

    [JsonPropertyName("signature")]
    public required string Signature { get; init; }

    public ExtensionRelease ToRelease()
        => new()
        {
            Version = Manifest.Version,
            MinHostVersion = Manifest.MinHostVersion,
            Url = Url,
            Size = Size,
            Sha256 = Sha256,
            Signature = new ExtensionPackageSignature { KeyId = KeyId, Signature = Signature }
        };
}

/// <summary>正式扩展发布 metadata v2；仅承担跨仓交接，ZIP 内 manifest 始终是最终权威源。</summary>
public sealed class ExtensionReleaseMetadataDocument
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("generatedAtUtc")]
    public required string GeneratedAtUtc { get; init; }

    [JsonPropertyName("packages")]
    public required IReadOnlyList<ExtensionReleaseMetadataPackage> Packages { get; init; } = Array.Empty<ExtensionReleaseMetadataPackage>();
}

/// <summary>严格解析 release-metadata.json schema v2，并复用 manifest/catalog v2 规则校验所有交接字段。</summary>
public static class ExtensionReleaseMetadataParser
{
    private const long MaximumPackageBytes = 209_715_200;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static ExtensionReleaseMetadataDocument Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ExtensionContractException("Extension release metadata 内容不能为空。");

        try
        {
            using var rawDocument = JsonDocument.Parse(json);
            var document = JsonSerializer.Deserialize<ExtensionReleaseMetadataDocument>(json, Options)
                ?? throw new JsonException("release metadata 内容为空。");
            ValidateDocument(document, rawDocument.RootElement);
            return document;
        }
        catch (JsonException exception)
        {
            throw new ExtensionContractException($"Extension release metadata JSON 不符合 schema v2：{exception.Message}");
        }
    }

    private static void ValidateDocument(ExtensionReleaseMetadataDocument document, JsonElement rawRoot)
    {
        if (document.SchemaVersion != 2)
            throw new ExtensionContractException("Extension release metadata schemaVersion 必须为 2。");
        if (!DateTimeOffset.TryParse(document.GeneratedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var generatedAt)
            || generatedAt.Offset != TimeSpan.Zero)
            throw new ExtensionContractException("Extension release metadata generatedAtUtc 必须是 UTC 时间。");
        if (document.Packages is null || document.Packages.Count == 0)
            throw new ExtensionContractException("Extension release metadata packages 不能为空。");

        if (!rawRoot.TryGetProperty("packages", out var rawPackages) || rawPackages.ValueKind != JsonValueKind.Array ||
            rawPackages.GetArrayLength() != document.Packages.Count)
        {
            throw new ExtensionContractException("Extension release metadata packages 必须是非空数组。");
        }

        var identities = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < document.Packages.Count; index++)
        {
            var package = document.Packages[index];
            if (package?.Manifest is null)
                throw new ExtensionContractException("Extension release metadata package 缺少 manifest。");

            var rawPackage = rawPackages[index];
            if (rawPackage.ValueKind != JsonValueKind.Object ||
                !rawPackage.TryGetProperty("manifest", out var rawManifest) ||
                rawManifest.ValueKind != JsonValueKind.Object)
            {
                throw new ExtensionContractException("Extension release metadata package 缺少 manifest。");
            }

            // 必须直接校验 metadata 中的原始 runtime 字段，避免 DTO 反序列化后丢失“字段是否出现”的信息。
            ValidateRuntimeShape(rawManifest);
            _ = ExtensionManifestParser.Parse(rawManifest.GetRawText(), ".");
            ValidateFileAndUrl(package);

            var release = package.ToRelease();
            var catalog = new ExtensionCatalogDocument
            {
                SchemaVersion = 2,
                Extensions =
                [
                    new ExtensionCatalogItem
                    {
                        Id = package.Manifest.Id,
                        Name = package.Manifest.Name,
                        Description = package.Manifest.Name,
                        PublisherId = package.Manifest.PublisherId,
                        Kind = package.Manifest.Kind,
                        Releases = [release]
                    }
                ]
            };
            _ = ExtensionCatalogParser.Parse(JsonSerializer.Serialize(catalog, Options));

            if (!identities.Add($"{package.Manifest.Id}\n{package.Manifest.Version}"))
                throw new ExtensionContractException($"Extension release metadata 包含重复版本：{package.Manifest.Id} {package.Manifest.Version}。");
        }

    }

    private static void ValidateRuntimeShape(JsonElement manifest)
    {
        if (!manifest.TryGetProperty("kind", out var kindElement) || kindElement.ValueKind != JsonValueKind.String ||
            !manifest.TryGetProperty("runtime", out var runtime) || runtime.ValueKind != JsonValueKind.Object ||
            !runtime.TryGetProperty("kind", out var runtimeKindElement) || runtimeKindElement.ValueKind != JsonValueKind.String)
        {
            return; // 缺失或类型错误由 manifest v2 解析器输出统一诊断。
        }

        var expectedFields = (kindElement.GetString(), runtimeKindElement.GetString()) switch
        {
            ("workspace", "web") => new[] { "kind", "entry" },
            ("analysis", "process") => new[] { "kind", "protocol", "entry" },
            ("analysis", "content") => new[] { "kind" },
            ("maintenance", "content") => new[] { "kind" },
            _ => null
        };
        if (expectedFields is null) return;

        var actualFields = runtime.EnumerateObject().Select(property => property.Name).ToArray();
        if (actualFields.Length != expectedFields.Length ||
            actualFields.Any(field => !expectedFields.Contains(field, StringComparer.Ordinal)))
        {
            throw new ExtensionContractException(
                $"Extension release metadata manifest.runtime 字段必须严格匹配 {kindElement.GetString()}/{runtimeKindElement.GetString()} 契约。");
        }
    }

    private static void ValidateFileAndUrl(ExtensionReleaseMetadataPackage package)
    {
        if (string.IsNullOrWhiteSpace(package.File)
            || package.File.Contains('/')
            || package.File.Contains('\\')
            || package.File.Contains("..", StringComparison.Ordinal)
            || Path.IsPathRooted(package.File)
            || !string.Equals(Path.GetFileName(package.File), package.File, StringComparison.Ordinal)
            || !string.Equals(Path.GetExtension(package.File), ".zip", StringComparison.OrdinalIgnoreCase))
            throw new ExtensionContractException($"Extension release metadata file 必须是安全 ZIP 文件名：{package.File}。");

        if (package.Size <= 0 || package.Size > MaximumPackageBytes)
            throw new ExtensionContractException($"Extension release metadata size 必须在 1 到 {MaximumPackageBytes} 字节之间。");

        if (!ExtensionContractValues.IsSafeHttpsReleaseUrl(package.Url) ||
            !Uri.TryCreate(package.Url, UriKind.Absolute, out var uri) ||
            !string.Equals(Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath)), package.File, StringComparison.Ordinal))
        {
            throw new ExtensionContractException("Extension release metadata url 必须是安全 HTTPS 地址并明确指向同名 ZIP 资产。");
        }
    }
}
