using HephaestusWorkbench.PluginSDK;

namespace HephaestusWorkbench.Services;

/// <summary>
/// 宿主根据 current.json 的验签密钥身份和当前 Trust Store 重新计算出的运行时授权快照。
/// 该对象不来自 manifest 自声明；集合均为防御性只读副本，调用方不能扩大受信任范围。
/// </summary>
public sealed class ExtensionRuntimeAuthorization
{
    internal ExtensionRuntimeAuthorization(
        string keyId,
        string publisherId,
        IEnumerable<ExtensionKind> allowedKinds,
        IEnumerable<string> permissions)
    {
        KeyId = keyId;
        PublisherId = publisherId;
        AllowedKinds = Array.AsReadOnly(allowedKinds.ToArray());
        Permissions = Array.AsReadOnly(permissions.ToArray());
    }

    public string KeyId { get; }

    public string PublisherId { get; }

    public IReadOnlyList<ExtensionKind> AllowedKinds { get; }

    public IReadOnlyList<string> Permissions { get; }
}
