using System.Text.Json;
using System.Text.Json.Serialization;
using HephaestusWorkbench.PluginSDK;

namespace HephaestusWorkbench.Tests;

public sealed class PluginManifestTests
{
    [Fact]
    public void LegacyManifest_ResolvesEntryRelativeToPluginDirectory()
    {
        var manifest = JsonSerializer.Deserialize<PluginManifest>("""
            { "id":"log-analyzer", "name":"日志分析插件", "version":"1.49", "type":"Exe", "entry":"log_analyzer.exe", "runner":"legacy-log-analyzer" }
            """, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } })!;
        var resolved = new PluginManifest
        {
            Id = manifest.Id,
            Name = manifest.Name,
            Version = manifest.Version,
            Type = manifest.Type,
            Entry = manifest.Entry,
            Runner = manifest.Runner,
            DirectoryPath = @"C:\WorkbenchData\Plugins\log-analyzer"
        };

        Assert.Equal("legacy-log-analyzer", resolved.Runner);
        Assert.Equal(Path.Combine(resolved.DirectoryPath, "log_analyzer.exe"), resolved.EntryPath);
    }
}
