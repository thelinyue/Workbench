using System.Text.Json;
using HephaestusWorkbench.PluginSDK;

namespace HephaestusWorkbench.Tests;

public sealed class ExtensionCurrentContractTests
{
    [Theory]
    [InlineData(ExtensionActivationState.Pending, "pending")]
    [InlineData(ExtensionActivationState.Healthy, "healthy")]
    public void CurrentDocument_UsesV2PendingHealthyState(ExtensionActivationState state, string expectedState)
    {
        var current = new ExtensionCurrentDocument
        {
            SchemaVersion = 2,
            Id = "log-analyzer",
            Version = "2.0.0",
            PackageSha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            TrustedKeyId = ExtensionTestTrust.DefaultKeyId,
            State = state
        };

        var json = JsonSerializer.Serialize(current);

        Assert.Contains("\"schemaVersion\":2", json, StringComparison.Ordinal);
        Assert.Contains($"\"state\":\"{expectedState}\"", json, StringComparison.Ordinal);
        Assert.Contains("\"packageSha256\":", json, StringComparison.Ordinal);
        Assert.Contains("\"trustedKeyId\":", json, StringComparison.Ordinal);
    }
}
