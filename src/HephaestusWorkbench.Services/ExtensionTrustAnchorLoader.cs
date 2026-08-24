using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HephaestusWorkbench.Services;

/// <summary>
/// 描述正式构建注入的 schema v2 扩展信任锚文档。
/// 该文档只负责建立宿主内置信任，Catalog 中声明的公钥不能通过此入口自行获得信任。
/// </summary>
internal sealed class ExtensionTrustAnchorDocument
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("trustedPublishers")]
    public required IReadOnlyList<TrustedPublisherKey> TrustedPublishers { get; init; }
}

/// <summary>
/// 从发布管线注入的 JSON 或程序集嵌入资源加载扩展信任锚。
/// 解析采用严格 schema：字段名区分大小写，任何未知字段都会失败，避免正式发布静默忽略错误配置。
/// </summary>
public static class ExtensionTrustAnchorLoader
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    /// <summary>严格解析 schema v2 信任锚并建立只读信任表。</summary>
    public static ExtensionTrustStore Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidDataException("扩展信任锚内容不能为空。");

        ExtensionTrustAnchorDocument document;
        try
        {
            document = JsonSerializer.Deserialize<ExtensionTrustAnchorDocument>(json, SerializerOptions)
                ?? throw new JsonException("扩展信任锚内容为空。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"扩展信任锚 JSON 不符合 schema v2：{exception.Message}", exception);
        }

        if (document.SchemaVersion != 2)
            throw new InvalidDataException($"扩展信任锚 schemaVersion 必须为 2，当前为 {document.SchemaVersion}。");
        if (document.TrustedPublishers is null || document.TrustedPublishers.Count == 0)
            throw new InvalidDataException("扩展信任锚 trustedPublishers 不能为空。");

        return new ExtensionTrustStore(document.TrustedPublishers);
    }

    /// <summary>
    /// 从指定程序集加载固定逻辑名的信任锚资源。源码开发构建可以将资源标记为可选；
    /// 正式 Bundle 构建必须标记为必需，缺失时立即以中文错误阻止启动。
    /// </summary>
    public static ExtensionTrustStore LoadEmbedded(Assembly assembly, string resourceName, bool required)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        if (string.IsNullOrWhiteSpace(resourceName))
            throw new ArgumentException("扩展信任锚资源名称不能为空。", nameof(resourceName));

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            if (!required)
                return new ExtensionTrustStore();

            throw new InvalidDataException($"正式构建缺少扩展信任锚资源：{resourceName}。");
        }

        try
        {
            using var reader = new StreamReader(stream);
            return Parse(reader.ReadToEnd());
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"读取扩展信任锚资源失败：{resourceName}。{exception.Message}", exception);
        }
    }
}
