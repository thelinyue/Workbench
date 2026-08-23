using System.Text.Json;
using HephaestusWorkbench.PluginSDK;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

public sealed class ExtensionTrustStoreTests
{
    [Fact]
    public void CustomStore_ResolvesTrustByKeyIdAndPreservesPublisherScope()
    {
        var trustedKey = new TrustedPublisherKey
        {
            KeyId = "test-key",
            PublisherId = "official",
            PublicKeyBase64 = Convert.ToBase64String(new byte[32]),
            Scope = new ExtensionTrustScope
            {
                AllowedKinds = [ExtensionKind.Analysis],
                Permissions = ["workspace.readText"]
            }
        };
        IExtensionTrustStore store = new ExtensionTrustStore([trustedKey]);

        Assert.True(store.TryGetTrustedKey("test-key", out var resolved));
        Assert.Equal("official", resolved.PublisherId);
        Assert.Equal([ExtensionKind.Analysis], resolved.Scope.AllowedKinds);
        Assert.Equal(["workspace.readText"], resolved.Scope.Permissions);
        Assert.False(store.TryGetTrustedKey("missing", out _));
    }

    [Fact]
    public void DefaultStore_ContainsHostOwnedOfficialTrustAnchor()
    {
        IExtensionTrustStore store = new ExtensionTrustStore();

        Assert.True(store.TryGetTrustedKey("official-2026", out var trustedKey));
        Assert.Equal("thelinyue", trustedKey.PublisherId);
        Assert.Equal(32, Convert.FromBase64String(trustedKey.PublicKeyBase64).Length);
        Assert.NotEmpty(trustedKey.Scope.AllowedKinds);
    }

    [Fact]
    public void TrustModels_AreJsonSerializable()
    {
        var trustedKey = new TrustedPublisherKey
        {
            KeyId = "test-key",
            PublisherId = "official",
            PublicKeyBase64 = Convert.ToBase64String(new byte[32]),
            Scope = new ExtensionTrustScope
            {
                AllowedKinds = [ExtensionKind.Workspace],
                Permissions = ["workspace.readText"]
            }
        };

        var json = JsonSerializer.Serialize(trustedKey);
        var restored = JsonSerializer.Deserialize<TrustedPublisherKey>(json);

        Assert.NotNull(restored);
        Assert.Equal(trustedKey.KeyId, restored.KeyId);
        Assert.Equal(trustedKey.Scope.Permissions, restored.Scope.Permissions);
    }
}
