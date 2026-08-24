using HephaestusWorkbench.PluginSDK;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

/// <summary>为不关注正式签名材料的单元测试提供显式宿主信任锚；生产组合根不会引用此类型。</summary>
internal static class ExtensionTestTrust
{
    public const string DefaultKeyId = "test-key";

    public static IExtensionTrustStore CreateStore(
        string publisherId = "thelinyue",
        string keyId = DefaultKeyId,
        IReadOnlyList<ExtensionKind>? allowedKinds = null,
        IReadOnlyList<string>? permissions = null)
        => new ExtensionTrustStore([
            new TrustedPublisherKey
            {
                KeyId = keyId,
                PublisherId = publisherId,
                PublicKeyBase64 = Convert.ToBase64String(new byte[32]),
                Scope = new ExtensionTrustScope
                {
                    AllowedKinds = allowedKinds ?? Enum.GetValues<ExtensionKind>(),
                    Permissions = permissions ?? ["workspace.readText", "workspace.writeText"]
                }
            }
        ]);
    public static IExtensionTrustStore CreateStoreForPublishers(
        params (string KeyId, string PublisherId)[] publishers)
        => new ExtensionTrustStore(publishers.Select(item => new TrustedPublisherKey
        {
            KeyId = item.KeyId,
            PublisherId = item.PublisherId,
            PublicKeyBase64 = Convert.ToBase64String(new byte[32]),
            Scope = new ExtensionTrustScope
            {
                AllowedKinds = Enum.GetValues<ExtensionKind>(),
                Permissions = ["workspace.readText", "workspace.writeText"]
            }
        }));
}
