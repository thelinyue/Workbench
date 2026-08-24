using System.Text.Json;
using System.Text.Json.Serialization;

namespace HephaestusWorkbench.PluginSDK;

/// <summary>扩展版本激活状态。Pending 表示仍需正式加载验证，Healthy 表示可供新任务使用。</summary>
[JsonConverter(typeof(LowerCamelCaseEnumConverter<ExtensionActivationState>))]
public enum ExtensionActivationState
{
    Pending,
    Healthy
}

/// <summary>
/// Extensions/&lt;id&gt;/current.json 的 v2 契约。packageSha256 用于拒绝相同 ID/版本对应不同内容，
/// trustedKeyId 绑定安装时实际完成验签的宿主信任密钥。回滚版本保存在同目录的 current.json.bak，并使用相同结构读取。
/// </summary>
public sealed class ExtensionCurrentDocument
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("packageSha256")]
    public required string PackageSha256 { get; init; }

    [JsonPropertyName("trustedKeyId")]
    public required string TrustedKeyId { get; init; }

    [JsonPropertyName("state")]
    public required ExtensionActivationState State { get; init; }
}

/// <summary>严格读取 current.json/current.json.bak，阻止路径式 ID、非法版本、错误哈希和未知字段进入 Registry。</summary>
public static class ExtensionCurrentParser
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static ExtensionCurrentDocument Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ExtensionContractException("扩展 current.json 内容不能为空。");
        }

        ExtensionCurrentDocument current;
        try
        {
            current = JsonSerializer.Deserialize<ExtensionCurrentDocument>(json, SerializerOptions)
                ?? throw new JsonException("扩展 current.json 内容为空。");
        }
        catch (JsonException exception) when (
            exception.Message.Contains("trustedKeyId", StringComparison.Ordinal))
        {
            throw new ExtensionContractException("扩展 current.json 缺少或无法读取 trustedKeyId。", exception);
        }
        catch (JsonException exception)
        {
            throw new ExtensionContractException($"扩展 current.json 不符合 v2 结构：{exception.Message}");
        }

        if (current.SchemaVersion != 2)
            throw new ExtensionContractException("扩展 current.json schemaVersion 必须为 2。");
        if (!ExtensionContractValues.IsIdentifier(current.Id))
            throw new ExtensionContractException("扩展 current.json id 无效。");
        if (!ExtensionContractValues.IsSemanticVersion(current.Version))
            throw new ExtensionContractException("扩展 current.json version 无效。");
        if (!ExtensionContractValues.IsSha256(current.PackageSha256))
            throw new ExtensionContractException("扩展 current.json packageSha256 无效。");
        if (string.IsNullOrWhiteSpace(current.TrustedKeyId))
            throw new ExtensionContractException("扩展 current.json 缺少 trustedKeyId。");

        return current;
    }
}
