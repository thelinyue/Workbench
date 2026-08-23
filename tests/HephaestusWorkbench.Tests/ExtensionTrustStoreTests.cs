using System.Text.Json;
using HephaestusWorkbench.PluginSDK;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

public sealed class ExtensionTrustStoreTests
{
    [Fact]
    public void CustomStore_ResolvesTrustByKeyIdAndPreservesPublisherScope()
    {
        var trustedKey = CreateTrustedKey();
        IExtensionTrustStore store = new ExtensionTrustStore([trustedKey]);

        Assert.True(store.TryGetTrustedKey("test-key", out var resolved));
        Assert.Equal("test-publisher", resolved.PublisherId);
        Assert.Equal([ExtensionKind.Analysis], resolved.Scope.AllowedKinds);
        Assert.Equal(["workspace.readText"], resolved.Scope.Permissions);
        Assert.False(store.TryGetTrustedKey("missing", out _));
    }

    [Fact]
    public void DefaultStore_HasNoTrustAnchorWhenFormalKeyIsUnavailable()
    {
        IExtensionTrustStore store = new ExtensionTrustStore();

        Assert.False(store.TryGetTrustedKey("official-2026", out _));
        Assert.False(store.TryGetTrustedKey("test-key", out _));
    }

    [Fact]
    public void Store_CopiesInputAndDoesNotExposeMutableScopeArrays()
    {
        var allowedKinds = new[] { ExtensionKind.Analysis };
        var permissions = new[] { "workspace.readText" };
        var store = new ExtensionTrustStore([CreateTrustedKey(allowedKinds, permissions)]);
        allowedKinds[0] = ExtensionKind.Workspace;
        permissions[0] = "mutated.input";

        Assert.True(store.TryGetTrustedKey("test-key", out var first));
        TryMutate(first.Scope.AllowedKinds, ExtensionKind.Maintenance);
        TryMutate(first.Scope.Permissions, "mutated.output");

        Assert.True(store.TryGetTrustedKey("test-key", out var second));
        Assert.Equal([ExtensionKind.Analysis], second.Scope.AllowedKinds);
        Assert.Equal(["workspace.readText"], second.Scope.Permissions);
    }

    [Fact]
    public void TrustModels_AreJsonSerializable()
    {
        var trustedKey = CreateTrustedKey();

        var json = JsonSerializer.Serialize(trustedKey);
        var restored = JsonSerializer.Deserialize<TrustedPublisherKey>(json);

        Assert.NotNull(restored);
        Assert.Equal(trustedKey.KeyId, restored.KeyId);
        Assert.Equal(trustedKey.Scope.Permissions, restored.Scope.Permissions);
    }

    private static TrustedPublisherKey CreateTrustedKey(
        IReadOnlyList<ExtensionKind>? allowedKinds = null,
        IReadOnlyList<string>? permissions = null)
        => new()
        {
            KeyId = "test-key",
            PublisherId = "test-publisher",
            PublicKeyBase64 = Convert.ToBase64String(new byte[32]),
            Scope = new ExtensionTrustScope
            {
                AllowedKinds = allowedKinds ?? [ExtensionKind.Analysis],
                Permissions = permissions ?? ["workspace.readText"]
            }
        };

    private static void TryMutate<T>(IReadOnlyList<T> values, T replacement)
    {
        if (values is not IList<T> mutable || mutable.Count == 0) return;
        try
        {
            mutable[0] = replacement;
        }
        catch (NotSupportedException)
        {
        }
    }
}
