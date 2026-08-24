using System.Diagnostics;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

public sealed class MaintenanceCatalogTests
{
    [Fact]
    public async Task LoadsStrictDefinitionsAsCoreSnapshots()
    {
        using var env = new CatalogEnv();
        env.WriteWorkflow(WorkflowJson());
        env.WriteProfile(ProfileJson());
        var catalog = env.Catalog();

        var workflow = await catalog.ResolveWorkflowAsync("storage-discovery");
        var profile = await catalog.ResolveCommandProfileAsync("linux.storage.discover");

        Assert.Equal(MaintenanceRiskLevel.ReadOnly, workflow.RiskLevel);
        Assert.Equal("storage.discover", Assert.Single(workflow.Steps).Action);
        Assert.Equal(MaintenanceCommandArgumentKind.Discovery, profile.Arguments[2].Kind);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("nested/path")]
    [InlineData("C:\\escape")]
    public async Task RejectsUnsafeIds(string id)
    {
        using var env = new CatalogEnv();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => env.Catalog().ResolveWorkflowAsync(id));
        Assert.Contains("标识", error.Message);
    }

    [Fact]
    public async Task RejectsUnknownFieldsAndOversizedFiles()
    {
        using var env = new CatalogEnv();
        env.WriteWorkflow(WorkflowJson().Replace(
            "\"name\": \"存储发现\",", "\"name\": \"存储发现\", \"unexpected\": true,"));
        Assert.Contains("结构", (await Assert.ThrowsAsync<InvalidDataException>(
            () => env.Catalog().ResolveWorkflowAsync("storage-discovery"))).Message);

        env.WriteWorkflow(new string('x', MaintenanceContentCatalog.MaximumDefinitionBytes + 1));
        Assert.Contains("大小", (await Assert.ThrowsAsync<InvalidDataException>(
            () => env.Catalog().ResolveWorkflowAsync("storage-discovery"))).Message);
    }

    [Fact]
    public async Task RejectsReparsePointDirectories()
    {
        using var env = new CatalogEnv();
        env.ReplaceWorkflowsWithJunction();
        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => env.Catalog().ResolveWorkflowAsync("storage-discovery"));
        Assert.Contains("重解析点", error.Message);
    }

    private static string WorkflowJson() => """
        {
          "schemaVersion": 2,
          "id": "storage-discovery",
          "name": "存储发现",
          "version": "1.0.0",
          "targetType": "linux-open-ssh",
          "riskLevel": "readOnly",
          "inputs": [],
          "steps": [{
            "id": "discover",
            "name": "发现块设备",
            "action": "storage.discover",
            "commandProfileId": "linux.storage.discover",
            "bindings": []
          }]
        }
        """;

    private static string ProfileJson() => """
        {
          "schemaVersion": 2,
          "id": "linux.storage.discover",
          "targetType": "linux-open-ssh",
          "action": "storage.discover",
          "executable": "/usr/bin/lsblk",
          "arguments": [
            { "kind": "literal", "value": "--json" },
            { "kind": "literal", "value": "--output" },
            { "kind": "discovery", "value": "columns" }
          ]
        }
        """;

    private sealed class CatalogEnv : IDisposable
    {
        private string? _junction;

        public CatalogEnv()
        {
            Root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, "workflows"));
            Directory.CreateDirectory(Path.Combine(Root, "command-profiles"));
        }

        public string Root { get; }
        public MaintenanceContentCatalog Catalog() => new(Root, "maintenance-pack", "1.0.0");
        public void WriteWorkflow(string json) => File.WriteAllText(Path.Combine(Root, "workflows", "storage-discovery.json"), json);
        public void WriteProfile(string json) => File.WriteAllText(Path.Combine(Root, "command-profiles", "linux.storage.discover.json"), json);

        public void ReplaceWorkflowsWithJunction()
        {
            var link = Path.Combine(Root, "workflows");
            Directory.Delete(link, true);
            var target = Path.Combine(Root, "outside-workflows");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "storage-discovery.json"), WorkflowJson());
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/d /c mklink /J \"{link}\" \"{target}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }) ?? throw new InvalidOperationException("无法创建测试 junction。");
            process.WaitForExit();
            if (process.ExitCode != 0 || !File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("无法创建测试 junction：" + process.StandardError.ReadToEnd());
            _junction = link;
        }

        public void Dispose()
        {
            if (_junction is not null && Directory.Exists(_junction) && File.GetAttributes(_junction).HasFlag(FileAttributes.ReparsePoint))
                Directory.Delete(_junction);
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
