using System.Text.Json;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Data;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

public sealed class ExtensionSettingsStoreTests
{
    [Fact]
    public async Task EnsureAsync_CreatesSchemaV2WithoutDefaultPluginSelection()
    {
        using var environment = new TestEnvironment();

        var settings = await environment.Store.EnsureAsync();
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(environment.Paths.ExtensionsConfigFile));

        Assert.Equal(2, settings.SchemaVersion);
        Assert.Equal("stable", settings.UpdateChannel);
        Assert.Equal("analysis.engine", settings.DefaultAnalysisCapability);
        Assert.True(json.RootElement.TryGetProperty("extensions", out _));
        Assert.False(json.RootElement.TryGetProperty("defaultPluginId", out _));
        Assert.False(json.RootElement.TryGetProperty("plugins", out _));
    }

    [Fact]
    public async Task SetEnabledAsync_UpsertsOnlyExtensionEnablement()
    {
        using var environment = new TestEnvironment();

        await environment.Store.SetEnabledAsync("log-analyzer", false);
        await environment.Store.SetEnabledAsync("log-analyzer", true);
        var settings = await environment.Store.EnsureAsync();

        var entry = Assert.Single(settings.Extensions);
        Assert.Equal("log-analyzer", entry.Id);
        Assert.True(entry.Enabled);
    }

    [Fact]
    public async Task EnsureAsync_RejectsUnknownOrLegacyFields()
    {
        using var environment = new TestEnvironment();
        Directory.CreateDirectory(Path.GetDirectoryName(environment.Paths.ExtensionsConfigFile)!);
        await File.WriteAllTextAsync(environment.Paths.ExtensionsConfigFile, """
            {
              "schemaVersion": 2,
              "updateChannel": "stable",
              "defaultAnalysisCapability": "analysis.engine",
              "extensions": [],
              "defaultPluginId": "legacy"
            }
            """);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => environment.Store.EnsureAsync());

        Assert.Contains("v2", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TestEnvironment : IDisposable
    {
        public TestEnvironment()
        {
            Root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
            Paths = new DataPaths(Root);
            Store = new ExtensionSettingsStore(Paths);
        }

        public string Root { get; }
        public DataPaths Paths { get; }
        public ExtensionSettingsStore Store { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
