using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

public sealed class MaintenancePlannerPolicyTests
{
    [Fact]
    public void PlannerBindsIndependentImmutableTokens()
    {
        var workflow = Workflow(
            MaintenanceRiskLevel.ReadOnly,
            [new("deviceName", "设备", "string", true)],
            [new("discover", "发现", "storage.discover", "profile",
                [new("device", "deviceName"), new("format", "outputFormat")])]);
        var profile = Profile("profile",
            [
                new(MaintenanceCommandArgumentKind.Literal, "--device"),
                new(MaintenanceCommandArgumentKind.Input, "device"),
                new(MaintenanceCommandArgumentKind.Discovery, "format")
            ]);

        var plan = new MaintenancePlanner().CreatePlan(Request(
            workflow, [profile],
            new Dictionary<string, string> { ["deviceName"] = "/dev/disk/by-id/test value" },
            new Dictionary<string, string> { ["outputFormat"] = "json" }));

        Assert.Equal(new[] { "--device", "/dev/disk/by-id/test value", "json" }, Assert.Single(plan.Steps).Arguments);
        Assert.Equal("major:minor=8:16", plan.Target.StableIdentity);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)plan.Steps[0].Arguments).Add("--mutate"));
        Assert.Throws<NotSupportedException>(() => ((IList<ExecutionStep>)plan.Steps).Add(plan.Steps[0]));
    }

    [Fact]
    public void PlannerRejectsMissingInputsUnknownBindingsAndDuplicateSteps()
    {
        var planner = new MaintenancePlanner();
        var required = Workflow(MaintenanceRiskLevel.ReadOnly,
            [new("required", "必填", "string", true)],
            [new("one", "步骤", "storage.discover", "profile", [])]);
        var profile = Profile("profile", []);
        Assert.Contains("必填", Assert.Throws<InvalidOperationException>(() => planner.CreatePlan(
            Request(required, [profile], new Dictionary<string, string>(), new Dictionary<string, string>()))).Message);

        var boundWorkflow = Workflow(MaintenanceRiskLevel.ReadOnly, [],
            [new("one", "步骤", "storage.discover", "bound", [new("value", "missing")])]);
        var bound = Profile("bound", [new(MaintenanceCommandArgumentKind.Discovery, "value")]);
        Assert.Contains("发现值", Assert.Throws<InvalidOperationException>(() => planner.CreatePlan(
            Request(boundWorkflow, [bound], new Dictionary<string, string>(), new Dictionary<string, string>()))).Message);

        var duplicate = required with
        {
            Inputs = [],
            Steps = [
                new("same", "一", "storage.discover", "profile", []),
                new("same", "二", "storage.discover", "profile", [])]
        };
        Assert.Contains("重复", Assert.Throws<InvalidOperationException>(() => planner.CreatePlan(
            Request(duplicate, [profile], new Dictionary<string, string>(), new Dictionary<string, string>()))).Message);
    }

    [Fact]
    public void PlannerRejectsMismatchedProfilesAndShellEntrypoints()
    {
        var workflow = Workflow(MaintenanceRiskLevel.ReadOnly, [],
            [new("one", "步骤", "storage.discover", "profile", [])]);
        var mismatch = Profile("profile", []) with { Action = "other.action" };
        Assert.Contains("action", Assert.Throws<InvalidOperationException>(() => new MaintenancePlanner().CreatePlan(
            Request(workflow, [mismatch], new Dictionary<string, string>(), new Dictionary<string, string>()))).Message);

        var shell = Profile("profile",
            [new(MaintenanceCommandArgumentKind.Literal, "-c"), new(MaintenanceCommandArgumentKind.Literal, "ls $(whoami)")]) with
        { Executable = "/bin/bash" };
        Assert.Contains("Shell", Assert.Throws<InvalidOperationException>(() => new MaintenancePlanner().CreatePlan(
            Request(workflow, [shell], new Dictionary<string, string>(), new Dictionary<string, string>()))).Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlannerRequiresStableIdentityToMatchExactlyOneTarget()
    {
        var workflow = Workflow(MaintenanceRiskLevel.ReadOnly, [],
            [new("one", "步骤", "storage.discover", "profile", [])]);
        var profile = Profile("profile", []);
        var zero = Request(workflow, [profile], new Dictionary<string, string>(), new Dictionary<string, string>()) with
        { SelectedStableIdentity = "missing" };
        Assert.Contains("唯一", Assert.Throws<InvalidOperationException>(() => new MaintenancePlanner().CreatePlan(zero)).Message);

        var multiple = Request(workflow, [profile], new Dictionary<string, string>(), new Dictionary<string, string>()) with
        {
            SelectedStableIdentity = "duplicate",
            Preflight = Preflight(new("block-device", "sdb", "duplicate"), new("block-device", "sdc", "duplicate"))
        };
        Assert.Contains("唯一", Assert.Throws<InvalidOperationException>(() => new MaintenancePlanner().CreatePlan(multiple)).Message);
    }

    [Fact]
    public void PolicyEnforcesTargetPrivilegeRiskConfirmationAndIdentityRecheck()
    {
        var policy = new MaintenancePolicy();
        var readOnly = Plan(MaintenanceRiskLevel.ReadOnly, false);
        Assert.True(policy.Evaluate(readOnly, Preflight(Target()), false).IsAllowed);
        Assert.False(policy.Evaluate(readOnly with { TargetType = "network-device" }, Preflight(Target()), false).IsAllowed);

        var unprivileged = Preflight(Target()) with { IsRoot = false, IsPasswordlessSudoAvailable = false };
        Assert.False(policy.Evaluate(readOnly, unprivileged, true).IsAllowed);
        Assert.True(policy.Evaluate(readOnly, unprivileged, false).IsAllowed);

        var high = Plan(MaintenanceRiskLevel.High, true);
        Assert.False(policy.VerifyExecution(high, Preflight(Target()), null, false).IsAllowed);
        Assert.False(policy.VerifyExecution(high, Preflight(Target()), "wrong", false).IsAllowed);
        Assert.True(policy.VerifyExecution(high, Preflight(Target()), "sdb", false).IsAllowed);
        Assert.False(policy.VerifyExecution(high,
            Preflight(new StableMaintenanceTarget("block-device", "sdb", "major:minor=8:32")), "sdb", false).IsAllowed);
        Assert.False(policy.Evaluate(high with { RiskLevel = (MaintenanceRiskLevel)999 }, Preflight(Target()), false).IsAllowed);
    }

    private static MaintenanceWorkflowSnapshot Workflow(MaintenanceRiskLevel risk,
        IReadOnlyList<MaintenanceWorkflowInputSnapshot> inputs,
        IReadOnlyList<MaintenanceWorkflowStepSnapshot> steps) =>
        new("workflow", "工作流", "1.0.0", "linux-open-ssh", risk, inputs, steps);

    private static MaintenanceCommandProfileSnapshot Profile(string id,
        IReadOnlyList<MaintenanceCommandArgumentTokenSnapshot> args) =>
        new(id, "linux-open-ssh", "storage.discover", "/usr/bin/lsblk", args);

    private static MaintenancePlanningRequest Request(MaintenanceWorkflowSnapshot workflow,
        IReadOnlyList<MaintenanceCommandProfileSnapshot> profiles,
        IReadOnlyDictionary<string, string> inputs,
        IReadOnlyDictionary<string, string> discovery) => new()
        {
            PlanId = "plan-1",
            Workflow = workflow,
            CommandProfiles = profiles.ToDictionary(item => item.Id, StringComparer.Ordinal),
            ExtensionId = "extension",
            ExtensionVersion = "1.0.0",
            DeviceId = "device-1",
            SelectedStableIdentity = "major:minor=8:16",
            UserInputs = inputs,
            DiscoveryValues = discovery,
            Preflight = Preflight(Target()),
            CreatedAt = DateTime.UtcNow
        };

    private static StableMaintenanceTarget Target() => new("block-device", "sdb", "major:minor=8:16");
    private static PreflightResult Preflight(params StableMaintenanceTarget[] targets) => new()
    {
        TargetType = "linux-open-ssh", RemoteUsername = "root", IsRoot = true,
        StableTargets = targets, Errors = [], Warnings = []
    };

    private static ExecutionPlan Plan(MaintenanceRiskLevel risk, bool confirm) => new()
    {
        Id = "plan", WorkflowId = "workflow", WorkflowVersion = "1.0.0",
        ExtensionId = "extension", ExtensionVersion = "1.0.0", DeviceId = "device-1",
        TargetType = "linux-open-ssh", RiskLevel = risk, Target = Target(),
        RequiresDeviceNameConfirmation = confirm, CreatedAt = DateTime.UtcNow,
        Steps = [new("step", 0, "步骤", "/usr/bin/true", [], risk == MaintenanceRiskLevel.ReadOnly)]
    };
}
