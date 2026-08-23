using HephaestusWorkbench.Data;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

public sealed class PluginProvisioningTests
{
    [Fact]
    public async Task ProvisionAsync_DoesNotDowngradeNewerInstalledPlugin()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        var seed = Path.Combine(root, "Seed");
        var paths = new DataPaths(Path.Combine(root, "Data"));
        var installed = Path.Combine(paths.ExtensionsDirectory, "log-analyzer");
        Directory.CreateDirectory(seed);
        Directory.CreateDirectory(installed);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(seed, "log_analyzer.exe"), "bundled");
            await File.WriteAllTextAsync(Path.Combine(seed, "manifest.json"), "{\"version\":\"1.50\"}");
            await File.WriteAllTextAsync(Path.Combine(installed, "log_analyzer.exe"), "online");
            await File.WriteAllTextAsync(Path.Combine(installed, "manifest.json"), "{\"version\":\"1.60\"}");

            await new PluginProvisioningService(paths, seed, new WorkbenchLogger(paths.Root)).ProvisionAsync();

            Assert.Equal("online", await File.ReadAllTextAsync(Path.Combine(installed, "log_analyzer.exe")));
            Assert.Contains("1.60", await File.ReadAllTextAsync(Path.Combine(installed, "manifest.json")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ProvisionAsync_UpdatesExecutableWhenManifestVersionChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        var seed = Path.Combine(root, "Seed");
        Directory.CreateDirectory(seed);
        try
        {
            var paths = new DataPaths(Path.Combine(root, "Data"));
            paths.EnsureCreated();
            await File.WriteAllTextAsync(Path.Combine(seed, "log_analyzer.exe"), "old");
            await File.WriteAllTextAsync(Path.Combine(seed, "manifest.json"), "{\"version\":\"1.49\"}");
            var service = new PluginProvisioningService(paths, seed, new WorkbenchLogger(paths.Root));

            await service.ProvisionAsync();

            await File.WriteAllTextAsync(Path.Combine(seed, "log_analyzer.exe"), "new");
            await File.WriteAllTextAsync(Path.Combine(seed, "manifest.json"), "{\"version\":\"1.50\"}");
            await service.ProvisionAsync();

            var destination = Path.Combine(paths.ExtensionsDirectory, "log-analyzer");
            Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(destination, "log_analyzer.exe")));
            Assert.Contains("1.50", await File.ReadAllTextAsync(Path.Combine(destination, "manifest.json")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ProvisionAsync_UpdatesExecutableWhenSameVersionBinaryChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        var seed = Path.Combine(root, "Seed");
        Directory.CreateDirectory(seed);
        try
        {
            var paths = new DataPaths(Path.Combine(root, "Data"));
            paths.EnsureCreated();
            await File.WriteAllTextAsync(Path.Combine(seed, "log_analyzer.exe"), "old");
            await File.WriteAllTextAsync(Path.Combine(seed, "manifest.json"), "{\"version\":\"1.60\"}");
            var service = new PluginProvisioningService(paths, seed, new WorkbenchLogger(paths.Root));

            await service.ProvisionAsync();

            await File.WriteAllTextAsync(Path.Combine(seed, "log_analyzer.exe"), "new-report-template");
            await service.ProvisionAsync();

            var destination = Path.Combine(paths.ExtensionsDirectory, "log-analyzer");
            Assert.Equal("new-report-template", await File.ReadAllTextAsync(Path.Combine(destination, "log_analyzer.exe")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
