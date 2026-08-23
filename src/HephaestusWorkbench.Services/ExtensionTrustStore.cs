using System.Text.Json.Serialization;
using HephaestusWorkbench.PluginSDK;

namespace HephaestusWorkbench.Services;

/// <summary>
/// 描述一个发布者密钥能够签发的扩展类别和 Workspace 权限。
/// 信任范围由宿主内置或宿主启动时注入，不能从在线 Catalog 构造。
/// </summary>
public sealed class ExtensionTrustScope
{
    [JsonPropertyName("allowedKinds")]
    public required IReadOnlyList<ExtensionKind> AllowedKinds { get; init; } = Array.Empty<ExtensionKind>();

    [JsonPropertyName("permissions")]
    public required IReadOnlyList<string> Permissions { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 宿主信任的一把 Ed25519 发布密钥。keyId 只负责索引，发布者身份和授权范围必须同时匹配。
/// </summary>
public sealed class TrustedPublisherKey
{
    [JsonPropertyName("keyId")]
    public required string KeyId { get; init; }

    [JsonPropertyName("publisherId")]
    public required string PublisherId { get; init; }

    [JsonPropertyName("publicKey")]
    public required string PublicKeyBase64 { get; init; }

    [JsonPropertyName("scope")]
    public required ExtensionTrustScope Scope { get; init; }
}

/// <summary>提供只读的宿主发布者信任锚；Catalog 不参与密钥或授权范围的解析。</summary>
public interface IExtensionTrustStore
{
    bool TryGetTrustedKey(string keyId, out TrustedPublisherKey trustedKey);
}

/// <summary>
/// 以内存字典保存宿主信任锚，查找关系固定为 keyId → publisherId → allowedKinds/permissions。
/// 默认构造函数只加载随宿主发布的官方公钥；可注入构造函数用于组合根或隔离测试。
/// </summary>
public sealed class ExtensionTrustStore : IExtensionTrustStore
{
    public const string OfficialKeyId = "official-2026";

    // RFC 8032 测试向量公钥仅作为当前仓库的宿主内置信任锚；正式发布密钥轮换时应替换此值并更换 keyId。
    private const string OfficialPublicKeyBase64 = "11qYAYKxCrfVS/7TyWQHOg7hcvPapiMlrwIaaPcHURo=";
    private readonly IReadOnlyDictionary<string, TrustedPublisherKey> _trustedKeys;

    public ExtensionTrustStore()
        : this(
        [
            new TrustedPublisherKey
            {
                KeyId = OfficialKeyId,
                PublisherId = "thelinyue",
                PublicKeyBase64 = OfficialPublicKeyBase64,
                Scope = new ExtensionTrustScope
                {
                    AllowedKinds = [ExtensionKind.Workspace, ExtensionKind.Analysis, ExtensionKind.Maintenance],
                    Permissions = ["workspace.readText"]
                }
            }
        ])
    {
    }

    public ExtensionTrustStore(IEnumerable<TrustedPublisherKey> trustedKeys)
    {
        ArgumentNullException.ThrowIfNull(trustedKeys);

        var entries = new Dictionary<string, TrustedPublisherKey>(StringComparer.Ordinal);
        foreach (var trustedKey in trustedKeys)
        {
            if (trustedKey is null)
                throw new InvalidDataException("扩展信任表不能包含空密钥记录。");
            ValidateTrustedKey(trustedKey);
            if (!entries.TryAdd(trustedKey.KeyId, Copy(trustedKey)))
                throw new InvalidDataException($"扩展信任表包含重复 keyId：{trustedKey.KeyId}。");
        }

        _trustedKeys = entries;
    }

    public bool TryGetTrustedKey(string keyId, out TrustedPublisherKey trustedKey)
    {
        if (!string.IsNullOrWhiteSpace(keyId) && _trustedKeys.TryGetValue(keyId, out var resolved))
        {
            trustedKey = resolved;
            return true;
        }

        trustedKey = null!;
        return false;
    }

    private static void ValidateTrustedKey(TrustedPublisherKey trustedKey)
    {
        if (string.IsNullOrWhiteSpace(trustedKey.KeyId))
            throw new InvalidDataException("扩展信任密钥缺少 keyId。");
        if (string.IsNullOrWhiteSpace(trustedKey.PublisherId))
            throw new InvalidDataException($"扩展信任密钥 {trustedKey.KeyId} 缺少 publisherId。");
        if (trustedKey.Scope?.AllowedKinds is null || trustedKey.Scope.Permissions is null)
            throw new InvalidDataException($"扩展信任密钥 {trustedKey.KeyId} 缺少授权范围。");

        try
        {
            if (Convert.FromBase64String(trustedKey.PublicKeyBase64 ?? string.Empty).Length != 32)
                throw new InvalidDataException($"扩展信任密钥 {trustedKey.KeyId} 的 Ed25519 公钥必须为 32 字节。");
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException($"扩展信任密钥 {trustedKey.KeyId} 的 Ed25519 公钥不是有效的 Base64。", exception);
        }
    }

    private static TrustedPublisherKey Copy(TrustedPublisherKey trustedKey)
        => new()
        {
            KeyId = trustedKey.KeyId,
            PublisherId = trustedKey.PublisherId,
            PublicKeyBase64 = trustedKey.PublicKeyBase64,
            Scope = new ExtensionTrustScope
            {
                AllowedKinds = trustedKey.Scope.AllowedKinds.ToArray(),
                Permissions = trustedKey.Scope.Permissions.ToArray()
            }
        };
}
