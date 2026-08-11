using System.Text.Json;
using HephaestusWorkbench.Data;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

public sealed class PluginCatalogTests
{
    [Fact]
    public async Task ScanAsync_FindsManifestAndExecutable()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        var pluginDirectory = Path.Combine(root, "Plugins", "sample");
        Directory.CreateDirectory(pluginDirectory);
        try
        {
            File.WriteAllText(Path.Combine(pluginDirectory, "sample.exe"), "test");
            File.WriteAllText(Path.Combine(pluginDirectory, "manifest.json"), JsonSerializer.Serialize(new
            {
                id = "sample",
                name = "测试插件",
                version = "1.0",
                type = "Exe",
                entry = "sample.exe"
            }));

            var paths = new DataPaths(root);
            var catalog = new PluginCatalog(paths, new WorkbenchLogger(root));
            var result = await catalog.ScanAsync();

            var plugin = Assert.Single(result);
            Assert.Equal("sample", plugin.Id);
            Assert.Equal(Path.Combine(pluginDirectory, "sample.exe"), plugin.EntryPath);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_IsolatesInvalidPluginAndReportsIssue()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        var pluginDirectory = Path.Combine(root, "Plugins", "broken");
        Directory.CreateDirectory(pluginDirectory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(pluginDirectory, "manifest.json"), """
                { "id":"broken", "name":"损坏插件", "version":"1.0", "type":"Exe", "entry":"missing.exe" }
                """);
            var catalog = new PluginCatalog(new DataPaths(root), new WorkbenchLogger(root));

            var result = await catalog.ScanAsync();

            Assert.Empty(result);
            Assert.Contains(catalog.Issues, issue => issue.Contains("插件入口不存在", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
