using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Repositories;
using HephaestusWorkbench.Core.Services;
using HephaestusWorkbench.Data;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

public sealed class MaintenanceExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_UsesIndependentStructuredCommandsAndPersistsSeparatedOutputs()
    {
        using var env = TestEnvironment.Create();
        var devices = new FakeDeviceRepository(Device());
        var commands = new FakeCommandExecutionService(
            CommandOutcome.Success("系统输出\n", "诊断警告\n", 0, TimeSpan.FromMilliseconds(25)),
            CommandOutcome.Success("第二步\n", "", 0, TimeSpan.FromMilliseconds(40)));
        var operations = new FakeOperationRepository();
        var executor = CreateExecutor(env.Paths, devices, commands, operations);

        var events = await CollectAsync(Execute(executor, Plan(
            Step("inspect", 0, "/usr/bin/lsblk", ["--json"], true),
            Step("verify", 1, "/usr/bin/findmnt", ["--json"], true))));

        Assert.Equal(2, commands.Requests.Count);
        Assert.All(commands.Requests, request =>
        {
            Assert.Equal("device-1", request.Connection.DeviceId);
            Assert.Equal("server.example", request.Connection.Host);
            Assert.Equal("HephaestusWorkbench/ssh/device-1/password", request.Connection.CredentialTarget);
        });
        Assert.Equal("/usr/bin/lsblk", commands.Requests[0].Executable);
        Assert.Equal(["--json"], commands.Requests[0].Arguments);
        Assert.All(commands.SuppliedCredentials, Assert.Null);

        var created = Assert.Single(operations.Created);
        Assert.Equal("Operations/plan-1", created.Operation.OperationDirectory);
        Assert.Equal(["--json"], created.Operation.Steps[0].Arguments);
        Assert.True(created.IsReadOnly);
        Assert.Equal("系统输出\n", await File.ReadAllTextAsync(Path.Combine(env.Paths.StorageRoot, created.Operation.Steps[0].StdoutPath ?? "missing")));
        Assert.Equal("诊断警告\n", await File.ReadAllTextAsync(Path.Combine(env.Paths.StorageRoot, created.Operation.Steps[0].StderrPath ?? "missing")));

        Assert.Equal(MaintenanceOperationStatus.Succeeded, operations.OperationUpdates[^1].Status);
        Assert.Equal(MaintenanceStepStatus.Succeeded, operations.StepUpdates[1].Status);
        Assert.Equal(0, operations.StepUpdates[1].ExitCode);
        Assert.Equal(TimeSpan.FromMilliseconds(25), operations.StepUpdates[1].Duration);
        Assert.Contains(events, item => item.OperationStatus == MaintenanceOperationStatus.Succeeded);
    }

    [Fact]
    public async Task ExecuteAsync_NonZeroExitStopsRemainingStepsAsKnownFailure()
    {
        using var env = TestEnvironment.Create();
        var commands = new FakeCommandExecutionService(CommandOutcome.Success("", "设备忙", 32, TimeSpan.FromSeconds(1)));
        var operations = new FakeOperationRepository();
        var executor = CreateExecutor(env.Paths, new FakeDeviceRepository(Device()), commands, operations);

        var events = await CollectAsync(Execute(executor, Plan(
            Step("first", 0, "/usr/bin/false", [], true),
            Step("must-not-run", 1, "/usr/bin/true", [], true))));

        Assert.Single(commands.Requests);
        Assert.Equal(MaintenanceStepStatus.Failed, operations.StepUpdates[^1].Status);
        Assert.Equal(32, operations.StepUpdates[^1].ExitCode);
        Assert.Equal(MaintenanceOperationStatus.Failed, operations.OperationUpdates[^1].Status);
        Assert.Contains("Exit Code 32", operations.OperationUpdates[^1].Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(events, item => item.StepId == "plan-1:must-not-run" && item.StepStatus == MaintenanceStepStatus.Running);
    }

    [Fact]
    public async Task ExecuteAsync_ConnectionLossMarksOutcomeUnknownAndNeverReplaysStep()
    {
        using var env = TestEnvironment.Create();
        var commands = new FakeCommandExecutionService(CommandOutcome.Throws(new IOException("连接已断开")));
        var operations = new FakeOperationRepository();
        var executor = CreateExecutor(env.Paths, new FakeDeviceRepository(Device()), commands, operations);

        var events = await CollectAsync(Execute(executor, Plan(Step("change", 0, "/usr/bin/test-tool", ["--device", "uuid-1"], false))));

        Assert.Single(commands.Requests);
        Assert.Equal(MaintenanceStepStatus.OutcomeUnknown, operations.StepUpdates[^1].Status);
        Assert.Equal(MaintenanceOperationStatus.OutcomeUnknown, operations.OperationUpdates[^1].Status);
        Assert.Contains("无法确认", operations.OperationUpdates[^1].Summary, StringComparison.Ordinal);
        Assert.Contains(events, item => item.OperationStatus == MaintenanceOperationStatus.OutcomeUnknown);
    }

    [Fact]
    public async Task ExecuteAsync_HighRiskCancellationWaitsForCurrentStepThenStopsBeforeNextStep()
    {
        using var env = TestEnvironment.Create();
        using var stop = new CancellationTokenSource();
        var commands = new FakeCommandExecutionService(
            CommandOutcome.CallbackThenSuccess(() => stop.Cancel(), "完成当前步骤", "", 0, TimeSpan.FromMilliseconds(10)));
        var operations = new FakeOperationRepository();
        var executor = CreateExecutor(env.Paths, new FakeDeviceRepository(Device()), commands, operations);
        var plan = Plan(MaintenanceRiskLevel.High,
            Step("change", 0, "/usr/bin/test-tool", ["--device", "uuid-1"], false),
            Step("postflight", 1, "/usr/bin/true", [], true));

        await CollectAsync(Execute(executor, plan, stop.Token));

        Assert.Single(commands.Requests);
        Assert.False(commands.ReceivedTokens[0].IsCancellationRequested);
        Assert.Equal(MaintenanceStepStatus.Succeeded, operations.StepUpdates[1].Status);
        Assert.Equal(MaintenanceOperationStatus.Failed, operations.OperationUpdates[^1].Status);
        Assert.Contains("停止", operations.OperationUpdates[^1].Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsTraversalOperationIdBeforeCreatingFilesOrDatabaseRows()
    {
        using var env = TestEnvironment.Create();
        var operations = new FakeOperationRepository();
        var executor = CreateExecutor(
            env.Paths,
            new FakeDeviceRepository(Device()),
            new FakeCommandExecutionService(),
            operations);
        var plan = Plan(Step("inspect", 0, "/usr/bin/true", [], true)) with { Id = "../escape" };

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => CollectAsync(Execute(executor, plan)));

        Assert.Contains("操作标识", error.Message, StringComparison.Ordinal);
        Assert.Empty(operations.Created);
        Assert.False(Directory.Exists(Path.Combine(env.Paths.StorageRoot, "escape")));
    }


    [Fact]
    public async Task ExecuteAsync_RechecksStableIdentityBeforeCreatingOperation()
    {
        using var env = TestEnvironment.Create();
        var commands = new FakeCommandExecutionService();
        var operations = new FakeOperationRepository();
        var discovery = new FakeDiscoveryService(Preflight(new StableMaintenanceTarget("block-device", "sdc", "uuid-other")));
        var executor = CreateExecutor(env.Paths, new FakeDeviceRepository(Device()), commands, operations, discovery);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CollectAsync(executor.ExecuteAsync(Request(Plan(Step("change", 0, "/usr/bin/test-tool", ["--device", "uuid-1"], false)), "sdb"))));

        Assert.Contains("身份复核", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, discovery.CallCount);
        Assert.Empty(commands.Requests);
        Assert.Empty(operations.Created);
    }

    private static MaintenanceExecutor CreateExecutor(
        DataPaths paths,
        ISshDeviceRepository devices,
        ICommandExecutionService commands,
        IMaintenanceOperationRepository operations,
        IMaintenanceDiscoveryService? discovery = null) =>
        new(paths, devices, discovery ?? new FakeDiscoveryService(Preflight()), new MaintenancePolicy(), commands, operations);

    private static IAsyncEnumerable<MaintenanceOperationEvent> Execute(
        MaintenanceExecutor executor,
        ExecutionPlan plan,
        CancellationToken cancellationToken = default) =>
        executor.ExecuteAsync(Request(plan, plan.RiskLevel == MaintenanceRiskLevel.High ? plan.Target.DisplayName : null), cancellationToken);

    private static MaintenanceExecutionRequest Request(ExecutionPlan plan, string? confirmationDisplayName = null) => new()
    {
        Plan = plan,
        ConfirmationDisplayName = confirmationDisplayName,
        Automatic = false
    };

    private static PreflightResult Preflight(params StableMaintenanceTarget[] targets) => new()
    {
        TargetType = "linux-open-ssh",
        RemoteUsername = "root",
        IsRoot = true,
        StableTargets = targets.Length == 0
            ? [new StableMaintenanceTarget("block-device", "sdb", "uuid-1")]
            : targets
    };

    private static async Task<List<MaintenanceOperationEvent>> CollectAsync(IAsyncEnumerable<MaintenanceOperationEvent> source)
    {
        var result = new List<MaintenanceOperationEvent>();
        await foreach (var item in source)
            result.Add(item);
        return result;
    }

    private static SshDevice Device() => new()
    {
        Id = "device-1",
        Name = "测试服务器",
        Host = "server.example",
        Port = 22,
        Username = "root",
        AuthenticationMethod = SshAuthenticationMethod.Password,
        CredentialTarget = "HephaestusWorkbench/ssh/device-1/password",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static ExecutionPlan Plan(params ExecutionStep[] steps) => Plan(MaintenanceRiskLevel.ReadOnly, steps);

    private static ExecutionPlan Plan(MaintenanceRiskLevel risk, params ExecutionStep[] steps) => new()
    {
        Id = "plan-1",
        WorkflowId = "workflow-1",
        WorkflowVersion = "1.0.0",
        ExtensionId = "maintenance-tests",
        ExtensionVersion = "1.0.0",
        DeviceId = "device-1",
        TargetType = "linux-open-ssh",
        RiskLevel = risk,
        RequiresDeviceNameConfirmation = risk == MaintenanceRiskLevel.High,
        Target = new StableMaintenanceTarget("block-device", "sdb", "uuid-1"),
        CreatedAt = DateTime.UtcNow,
        Steps = steps
    };

    private static ExecutionStep Step(string id, int index, string executable, IReadOnlyList<string> arguments, bool readOnly) =>
        new(id, index, id, executable, arguments, readOnly);

    private sealed class TestEnvironment : IDisposable
    {
        private TestEnvironment(string root) { Root = root; Paths = new DataPaths(root); }
        public string Root { get; }
        public DataPaths Paths { get; }
        public static TestEnvironment Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new TestEnvironment(root);
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    private sealed class FakeDeviceRepository(SshDevice? device) : ISshDeviceRepository
    {
        public Task<IReadOnlyList<SshDevice>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SshDevice>>(device is null ? [] : [device]);
        public Task<SshDevice?> GetAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(device?.Id == id ? device : null);
        public Task UpsertAsync(SshDevice value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }


    private sealed class FakeDiscoveryService(PreflightResult result) : IMaintenanceDiscoveryService
    {
        public int CallCount { get; private set; }
        public Task<PreflightResult> DiscoverAsync(
            string targetType,
            SshConnectionRequest connection,
            SshCredentialSecret? credential,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeCommandExecutionService(params CommandOutcome[] outcomes) : ICommandExecutionService
    {
        private readonly Queue<CommandOutcome> _outcomes = new(outcomes);
        public List<RemoteCommandRequest> Requests { get; } = [];
        public List<SshCredentialSecret?> SuppliedCredentials { get; } = [];
        public List<CancellationToken> ReceivedTokens { get; } = [];

        public async Task<RemoteCommandResult> ExecuteAsync(
            RemoteCommandRequest request,
            SshCredentialSecret? credential,
            Func<RemoteCommandOutputChunk, CancellationToken, ValueTask> onOutput,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            SuppliedCredentials.Add(credential);
            ReceivedTokens.Add(cancellationToken);
            var outcome = _outcomes.Count == 0 ? CommandOutcome.Success("", "", 0, TimeSpan.Zero) : _outcomes.Dequeue();
            outcome.BeforeResult?.Invoke();
            if (outcome.Error is not null)
                throw outcome.Error;
            if (outcome.Stdout.Length > 0)
                await onOutput(new RemoteCommandOutputChunk(1, RemoteCommandOutputStream.Stdout, outcome.Stdout), cancellationToken);
            if (outcome.Stderr.Length > 0)
                await onOutput(new RemoteCommandOutputChunk(2, RemoteCommandOutputStream.Stderr, outcome.Stderr), cancellationToken);
            return new RemoteCommandResult(outcome.ExitCode, outcome.Duration);
        }
    }

    private sealed record CommandOutcome(string Stdout, string Stderr, int ExitCode, TimeSpan Duration, Exception? Error, Action? BeforeResult)
    {
        public static CommandOutcome Success(string stdout, string stderr, int exitCode, TimeSpan duration) => new(stdout, stderr, exitCode, duration, null, null);
        public static CommandOutcome CallbackThenSuccess(Action callback, string stdout, string stderr, int exitCode, TimeSpan duration) => new(stdout, stderr, exitCode, duration, null, callback);
        public static CommandOutcome Throws(Exception error) => new("", "", 0, TimeSpan.Zero, error, null);
    }

    private sealed class FakeOperationRepository : IMaintenanceOperationRepository
    {
        public List<(MaintenanceOperation Operation, bool IsReadOnly)> Created { get; } = [];
        public List<(string Id, MaintenanceOperationStatus Status, DateTime? CompletedAt, string? Summary)> OperationUpdates { get; } = [];
        public List<MaintenanceOperationStepUpdate> StepUpdates { get; } = [];

        public Task CreateAsync(MaintenanceOperation operation, bool isReadOnly, CancellationToken cancellationToken = default)
        {
            Created.Add((operation, isReadOnly));
            return Task.CompletedTask;
        }
        public Task<MaintenanceOperation?> GetAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult<MaintenanceOperation?>(null);
        public Task<IReadOnlyList<MaintenanceOperation>> ListRecentAsync(int limit, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MaintenanceOperation>>([]);
        public Task UpdateOperationAsync(string id, MaintenanceOperationStatus status, DateTime? completedAt, string? outcomeSummary, CancellationToken cancellationToken = default)
        {
            OperationUpdates.Add((id, status, completedAt, outcomeSummary));
            return Task.CompletedTask;
        }
        public Task UpdateStepAsync(MaintenanceOperationStepUpdate update, CancellationToken cancellationToken = default)
        {
            StepUpdates.Add(update);
            return Task.CompletedTask;
        }
        public Task<bool> HasActiveOperationAsync(string deviceId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<int> RecoverInterruptedAsync(DateTime recoveredAt, CancellationToken cancellationToken = default) => Task.FromResult(0);
    }
}
