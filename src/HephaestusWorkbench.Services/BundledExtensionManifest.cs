using System.Text.Json;
using System.Text.Json.Serialization;
using HephaestusWorkbench.PluginSDK;

namespace HephaestusWorkbench.Services;

/// <summary>安装包内置扩展的锁定清单；只保存公开发布元数据，不包含或授予任何信任密钥。</summary>
public sealed class BundledExtensionDocument
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("extensions")]
    public required IReadOnlyList<BundledExtensionItem> Extensions { get; init; } = Array.Empty<BundledExtensionItem>();
}

/// <summary>一份离线 ZIP 与既有 Catalog v2 身份、版本和签名元数据的绑定。</summary>
public sealed class BundledExtensionItem
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

    [JsonPropertyName("asset")]
    public required string Asset { get; init; }

    [JsonPropertyName("release")]
    public required ExtensionRelease Release { get; init; }

    public ExtensionCatalogItem ToCatalogItem()
        => new()
        {
            Id = Id,
            Name = Name,
            Description = Description,
            PublisherId = PublisherId,
            Kind = Kind,
            Releases = [Release]
        };
}

/// <summary>
/// 严格读取发行锁定清单。核心发布字段继续交给 Catalog v2 解析器复核，
/// 本解析器只增加本地 asset 文件名、数量上限和唯一性约束。
/// </summary>
public static class BundledExtensionManifestParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³"
    };

    public static BundledExtensionDocument Parse(string json)
    {
        BundledExtensionDocument document;
        try
        {
            document = JsonSerializer.Deserialize<BundledExtensionDocument>(json, Options)
                       ?? throw new JsonException("清单内容为空。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Bundled Extension 清单 JSON 不符合 v2 结构：{exception.Message}", exception);
        }

        if (document.SchemaVersion != 2)
            throw new InvalidDataException("Bundled Extension 清单 schemaVersion 必须为 2。");
        if (document.Extensions is null || document.Extensions.Count == 0)
            throw new InvalidDataException("Bundled Extension 清单 extensions 不能为空。");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var assets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in document.Extensions)
        {
            if (item is null)
                throw new InvalidDataException("Bundled Extension 清单不能包含空扩展项。");
            if (!ids.Add(item.Id ?? string.Empty))
                throw new InvalidDataException($"Bundled Extension 清单包含重复扩展 ID：{item.Id}。");
            ValidateAsset(item.Asset);
            if (item.Release is null)
                throw new InvalidDataException($"Bundled Extension {item.Id} 的 release 不能为空。");
            if (!assets.Add(item.Asset))
                throw new InvalidDataException($"Bundled Extension 清单包含重复 asset：{item.Asset}。");
            if (item.Release.Size <= 0 || item.Release.Size > ExtensionPackageLimits.MaximumPackageBytes)
                throw new InvalidDataException($"Bundled Extension {item.Id} 的 size 必须在 1 到 {ExtensionPackageLimits.MaximumPackageBytes} 字节之间。");
        }

        try
        {
            var catalog = new ExtensionCatalogDocument
            {
                SchemaVersion = 2,
                Extensions = document.Extensions.Select(item => item.ToCatalogItem()).ToArray()
            };
            _ = ExtensionCatalogParser.Parse(JsonSerializer.Serialize(catalog));
        }
        catch (ExtensionContractException exception)
        {
            throw new InvalidDataException($"Bundled Extension 清单的发布元数据无效：{exception.Message}", exception);
        }

        return document;
    }

    private static void ValidateAsset(string? asset)
    {
        if (string.IsNullOrWhiteSpace(asset)
            || asset.Contains('/')
            || asset.Contains('\\')
            || asset.Contains("..", StringComparison.Ordinal)
            || Path.IsPathRooted(asset)
            || !string.Equals(Path.GetFileName(asset), asset, StringComparison.Ordinal)
            || !string.Equals(Path.GetExtension(asset), ".zip", StringComparison.OrdinalIgnoreCase)
            || asset.Any(character => character < 32 || Path.GetInvalidFileNameChars().Contains(character)))
        {
            throw new InvalidDataException($"Bundled Extension asset 必须是安全的 ZIP 文件名：{asset}");
        }

        var deviceName = Path.GetFileNameWithoutExtension(asset).Split('.', 2)[0].TrimEnd(' ', '.');
        if (WindowsReservedNames.Contains(deviceName))
            throw new InvalidDataException($"Bundled Extension asset 使用了 Windows 保留名称：{asset}");
    }
}
