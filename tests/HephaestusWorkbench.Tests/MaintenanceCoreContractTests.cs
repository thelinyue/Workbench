using System.Text.Json;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Repositories;
using HephaestusWorkbench.Core.Services;

namespace HephaestusWorkbench.Tests;

public sealed class MaintenanceCoreContractTests
{
    [Fact]
    public void CoreMaintenanceContracts_AreJsonSerializableAndDoNotReferenceImplementationAssemblies()
    {
        var preflight = new PreflightResult
        {
            TargetType = "linux-open-ssh",
            RemoteUsername = "root",
            IsRoot = true,
            IsPasswordlessSudoAvailable = false,
            StableTargets = [new StableMaintenanceTarget("block-device", "sdb", "major:minor=8:16")],
            Errors = [],
            Warnings = ["测试告警"]
        };
        var plan = new ExecutionPlan
        {
            Id = "plan-1",
            WorkflowId = "storage-discovery",
            WorkflowVersion = "1.0.0",
            ExtensionId = "maintenance-pack",
            ExtensionVersion = "1.0.0",
            DeviceId = "device-1",
            TargetType = "linux-open-ssh",
            RiskLevel = MaintenanceRiskLevel.ReadOnly,
            Target = preflight.StableTargets[0],
            RequiresDeviceNameConfirmation = false,
            CreatedAt = DateTime.UtcNow,
            Steps = [new ExecutionStep("discover", 0, "发现设备", "/usr/bin/lsblk", ["--json"], true)]
        };

        var json = JsonSerializer.Serialize(plan);

        Assert.Contains("major:minor=8:16", json, StringComparison.Ordinal);
        Assert.DoesNotContain("PluginSDK", json, StringComparison.Ordinal);
        Assert.Equal("HephaestusWorkbench.Core", typeof(IMaintenancePlanner).Assembly.GetName().Name);
        Assert.Equal("HephaestusWorkbench.Core", typeof(IMaintenanceOperationRepository).Assembly.GetName().Name);
        Assert.All(typeof(ExecutionPlan).Assembly.GetReferencedAssemblies(), reference =>
            Assert.DoesNotContain(reference.Name!, new[]
            {
                "HephaestusWorkbench.PluginSDK", "Microsoft.Data.Sqlite", "Renci.SshNet", "PresentationFramework"
            }));
    }
}
