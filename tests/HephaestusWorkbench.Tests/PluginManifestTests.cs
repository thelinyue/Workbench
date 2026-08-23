using System.Text.Json;
using System.Text.Json.Serialization;
using HephaestusWorkbench.PluginSDK;

namespace HephaestusWorkbench.Tests;

public sealed class PluginManifestTests
{
    [Fact]
    public void BundledSystemDiagnosisPlugin_UsesNewDisplayNameAndKeepsLegacyId()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "PluginSeed", "manifest.json");
        var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(path), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        })!;

        Assert.Equal("系统诊断插件", manifest.Name);
        Assert.Equal("log-analyzer", manifest.Id);
        Assert.Equal("log_analyzer.exe", manifest.Entry);
    }

    [Fact]
    public void LegacyManifest_ResolvesEntryRelativeToPluginDirectory()
    {
        var manifest = JsonSerializer.Deserialize<PluginManifest>("""
            { "id":"log-analyzer", "name":"日志分析", "version":"1.49", "type":"Exe", "entry":"log_analyzer.exe", "runner":"legacy-log-analyzer" }
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
