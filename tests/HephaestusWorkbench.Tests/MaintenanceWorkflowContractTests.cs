using System.Text.Json;
using HephaestusWorkbench.PluginSDK;

namespace HephaestusWorkbench.Tests;

public sealed class MaintenanceWorkflowContractTests
{
    [Fact]
    public void Workflow_DeclaresActionsWithoutEmbeddingCommands()
    {
        var workflow = new WorkflowDefinition
        {
            SchemaVersion = 2,
            Id = "storage-discovery",
            Name = "存储发现",
            Version = "1.0.0",
            TargetType = "linux-open-ssh",
            RiskLevel = MaintenanceRiskLevels.ReadOnly,
            Inputs = Array.Empty<WorkflowInputDefinition>(),
            Steps =
            [
                new WorkflowStepDefinition
                {
                    Id = "discover-block-devices",
                    Name = "发现块设备",
                    Action = "storage.discover-block-devices",
                    CommandProfileId = "linux.storage.discover-block-devices",
                    Bindings = Array.Empty<WorkflowArgumentBinding>()
                }
            ]
        };

        var json = JsonSerializer.Serialize(workflow);

        Assert.Contains("\"action\":\"storage.discover-block-devices\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"executable\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("sh -c", json, StringComparison.Ordinal);
    }

    [Fact]
    public void CommandProfile_RepresentsArgumentsAsIndependentTokens()
    {
        var profile = new CommandProfile
        {
            SchemaVersion = 2,
            Id = "linux.storage.discover-block-devices",
            TargetType = "linux-open-ssh",
            Action = "storage.discover-block-devices",
            Executable = "/usr/bin/lsblk",
            Arguments =
            [
                new CommandArgumentToken { Kind = CommandArgumentKinds.Literal, Value = "--json" },
                new CommandArgumentToken { Kind = CommandArgumentKinds.Input, Value = "deviceId" }
            ]
        };

        var json = JsonSerializer.Serialize(profile);

        Assert.Contains("\"executable\":\"/usr/bin/lsblk\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"input\",\"value\":\"deviceId\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"command\"", json, StringComparison.Ordinal);
    }
}
