using System.Text.Json;
using System.Text.Json.Serialization;

namespace HephaestusWorkbench.PluginSDK;

/// <summary>
/// 扩展所属的宿主管理类别。类别决定运行时形态和可申请的能力，扩展不能借此贡献客户端导航。
/// </summary>
[JsonConverter(typeof(LowerCamelCaseEnumConverter<ExtensionKind>))]
public enum ExtensionKind
{
    Workspace,
    Analysis,
    Maintenance
}

/// <summary>
/// 扩展运行时形态。正式版只允许受控 Web 页面、独立进程和纯内容包。
/// </summary>
[JsonConverter(typeof(LowerCamelCaseEnumConverter<ExtensionRuntimeKind>))]
public enum ExtensionRuntimeKind
{
    Web,
    Process,
    Content
}

/// <summary>
/// manifest v2 的运行时声明。入口始终相对于扩展版本目录解析，不接受绝对路径。
/// </summary>
public sealed class ExtensionRuntime
{
    [JsonPropertyName("kind")]
    public required ExtensionRuntimeKind Kind { get; init; }

    [JsonPropertyName("protocol")]
    public string? Protocol { get; init; }

    [JsonPropertyName("entry")]
    public string? Entry { get; init; }
}

/// <summary>扩展对另一扩展版本的显式依赖。</summary>
public sealed class ExtensionDependency
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }
}

/// <summary>
/// Hephaestus Workbench 扩展清单 v2。该类型仅描述可序列化契约，不暴露 WPF、数据库或进程实现。
/// </summary>
public sealed class ExtensionManifest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("kind")]
    public required ExtensionKind Kind { get; init; }

    [JsonPropertyName("publisherId")]
    public required string PublisherId { get; init; }

    [JsonPropertyName("hostApiVersion")]
    public required string HostApiVersion { get; init; }

    [JsonPropertyName("minHostVersion")]
    public required string MinHostVersion { get; init; }

    [JsonPropertyName("runtime")]
    public required ExtensionRuntime Runtime { get; init; }

    [JsonPropertyName("capabilities")]
    public required IReadOnlyList<string> Capabilities { get; init; } = Array.Empty<string>();

    [JsonPropertyName("permissions")]
    public required IReadOnlyList<string> Permissions { get; init; } = Array.Empty<string>();

    [JsonPropertyName("dependencies")]
    public required IReadOnlyList<ExtensionDependency> Dependencies { get; init; } = Array.Empty<ExtensionDependency>();

    [JsonIgnore]
    public string DirectoryPath { get; init; } = string.Empty;

    [JsonIgnore]
    public string? EntryPath => string.IsNullOrWhiteSpace(Runtime.Entry)
        ? null
        : Path.GetFullPath(Path.Combine(DirectoryPath, Runtime.Entry));

    public bool SupportsCapability(string capability)
        => Capabilities.Any(item => string.Equals(item, capability, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// 负责从原始 JSON 建立 manifest v2 对象。集中持有 JSON 命名规则，避免不同宿主模块各自解释协议。
/// </summary>
public static class ExtensionManifestParser
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public static ExtensionManifest Parse(string json, string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ExtensionContractException("扩展清单内容不能为空。");
        if (string.IsNullOrWhiteSpace(directoryPath))
            throw new ExtensionContractException("扩展版本目录不能为空。");

        ExtensionManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ExtensionManifest>(json, SerializerOptions)
                ?? throw new JsonException("扩展清单内容为空。");
        }
        catch (JsonException exception)
        {
            throw new ExtensionContractException($"扩展清单 JSON 不符合 v2 结构：{exception.Message}");
        }

        var resolvedManifest = new ExtensionManifest
        {
            SchemaVersion = manifest.SchemaVersion,
            Id = manifest.Id,
            Name = manifest.Name,
            Version = manifest.Version,
            Kind = manifest.Kind,
            PublisherId = manifest.PublisherId,
            HostApiVersion = manifest.HostApiVersion,
            MinHostVersion = manifest.MinHostVersion,
            Runtime = manifest.Runtime,
            Capabilities = manifest.Capabilities,
            Permissions = manifest.Permissions,
            Dependencies = manifest.Dependencies,
            DirectoryPath = NormalizeDirectoryPath(directoryPath)
        };

        ExtensionContractValidator.ValidateManifest(resolvedManifest);
        return resolvedManifest;
    }

    private static string NormalizeDirectoryPath(string directoryPath)
    {
        try
        {
            return Path.GetFullPath(directoryPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            throw new ExtensionContractException($"扩展版本目录无效：{exception.Message}", exception);
        }
    }
}
