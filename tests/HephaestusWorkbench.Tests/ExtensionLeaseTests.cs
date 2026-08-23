using System.Text.Json;
using HephaestusWorkbench.PluginSDK;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

public sealed class ExtensionLeaseTests
{
    [Fact]
    public async Task LeaseCurrentVersion_IsolatesTaskVersionUntilDisposed()
    {
        using var layout = new ExtensionTestLayout();
        layout.WriteManifest("sample", "1.0.0");
        layout.WriteManifest("sample", "2.0.0");
        layout.WriteCurrent("sample", "1.0.0", ExtensionTestLayout.HashA, ExtensionActivationState.Healthy);
        var registry = new ExtensionRegistry(layout.ExtensionsRoot, new PassingHealthChecker());
        await registry.LoadAsync();

        var lease = registry.LeaseCurrentVersion("sample");
        layout.WriteCurrent("sample", "2.0.0", ExtensionTestLayout.HashB, ExtensionActivationState.Healthy);
        if (File.Exists(layout.BackupPath("sample")))
            File.Delete(layout.BackupPath("sample"));
        await registry.LoadAsync();

        Assert.Equal("1.0.0", lease.Version);
        Assert.False(registry.CanDeleteVersion("sample", "1.0.0"));
        using (var currentLease = registry.LeaseCurrentVersion("sample"))
            Assert.Equal("2.0.0", currentLease.Version);

        lease.Dispose();

        Assert.True(registry.CanDeleteVersion("sample", "1.0.0"));
    }

    [Fact]
    public async Task CanDeleteVersion_ProtectsCurrentAndRollbackDocuments()
    {
        using var layout = new ExtensionTestLayout();
        layout.WriteManifest("sample", "1.0.0");
        layout.WriteManifest("sample", "2.0.0");
        layout.WriteCurrent("sample", "2.0.0", ExtensionTestLayout.HashB, ExtensionActivationState.Healthy);
        layout.WriteBackup("sample", "1.0.0", ExtensionTestLayout.HashA, ExtensionActivationState.Healthy);
        var registry = new ExtensionRegistry(layout.ExtensionsRoot, new PassingHealthChecker());
        await registry.LoadAsync();

        Assert.False(registry.CanDeleteVersion("sample", "2.0.0"));
        Assert.False(registry.CanDeleteVersion("sample", "1.0.0"));
        Assert.True(registry.CanDeleteVersion("sample", "3.0.0"));
    }

    private sealed class PassingHealthChecker : IExtensionHealthChecker
    {
        public Task CheckAsync(ExtensionManifest manifest, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
