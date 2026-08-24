using System.Runtime.CompilerServices;
using HephaestusWorkbench.App.ViewModels;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Repositories;
using HephaestusWorkbench.Core.Services;
using HephaestusWorkbench.Data;

namespace HephaestusWorkbench.Tests;

public sealed class MaintenanceWorkspaceViewModelTests
{

    [Fact]
    public async Task Initialization_FiltersHistoryByDeviceAndProjectsAuditDetails()
    {
        using var environment = TestEnvironment.Create();
        var repository = new FakeOperationRepository(
            Operation("op-target", "device-a", MaintenanceOperationStatus.Failed,
                Step("op-target", "step-a", MaintenanceStepStatus.Failed, exitCode: 8, duration: TimeSpan.FromSeconds(3))),
            Operation("op-other", "device-b", MaintenanceOperationStatus.Succeeded));

        using var viewModel = CreateViewModel(environment.Paths, repository: repository);
        await viewModel.Initialization;

        var history = Assert.Single(viewModel.History);
        Assert.Equal("op-target", history.OperationId);
        Assert.Equal("2.4.0", history.ExtensionVersion);
        Assert.Equal("失败", history.StatusText);
        Assert.Equal(new DateTime(2026, 8, 24, 1, 2, 3, DateTimeKind.Utc), history.StartedAt);
        var step = Assert.Single(history.Steps);
        Assert.Equal("失败", step.StatusText);
        Assert.Equal(8, step.ExitCode);
        Assert.Equal(TimeSpan.FromSeconds(3), step.Duration);
    }

    [Fact]
    public async Task PlanSteps_AreReadOnly_AndClipboardUsesExplicitPosixTokenQuoting()
    {
        using var environment = TestEnvironment.Create();
        string? copied = null;
        using var viewModel = CreateViewModel(
            environment.Paths,
            plan: Plan(MaintenanceRiskLevel.ReadOnly,
                new ExecutionStep("step-a", 0, "读取设备", "printf", ["a b", "x'y", ""], true)),
            clipboardWriter: value => copied = value);
        await viewModel.Initialization;

        var step = Assert.Single(viewModel.PlanSteps);
        Assert.Null(typeof(MaintenancePlanStepViewModel).GetProperty(nameof(MaintenancePlanStepViewModel.Executable))!.SetMethod);
        Assert.Null(typeof(MaintenancePlanStepViewModel).GetProperty(nameof(MaintenancePlanStepViewModel.Arguments))!.SetMethod);

        viewModel.CopyCommand(step);

        Assert.Equal("'printf' 'a b' 'x'\"'\"'y' ''", copied);
        Assert.Equal("命令已复制。复制内容仅供核对，不会由界面直接执行。", viewModel.StatusMessage);
    }

    [Fact]
    public async Task ExecuteRequiresAcknowledgementAndExactHighRiskTargetName()
    {
        using var environment = TestEnvironment.Create();
        var executor = new RecordingExecutor([]);
        using var viewModel = CreateViewModel(
            environment.Paths,
            plan: Plan(MaintenanceRiskLevel.High, new ExecutionStep("step-a", 0, "高风险测试", "true", [], false)),
            executor: executor);
        await viewModel.Initialization;

        Assert.True(viewModel.RequiresConfirmationText);
        Assert.False(viewModel.CanExecute);

        viewModel.PlanAcknowledged = true;
        viewModel.ConfirmationText = "/dev/other";
        Assert.False(viewModel.CanExecute);

        viewModel.ConfirmationText = "/dev/sda";
        Assert.True(viewModel.CanExecute);
        await viewModel.ExecuteAsync();

        var request = Assert.Single(executor.Requests);
        Assert.False(request.Automatic);
        Assert.Equal("/dev/sda", request.ConfirmationDisplayName);
    }

    [Fact]
    public async Task ExecuteStreamsEventsIntoReadOnlyPlanAndRefreshesHistory()
    {
        using var environment = TestEnvironment.Create();
        var repository = new FakeOperationRepository();
        var executor = new RecordingExecutor(
        [
            new MaintenanceOperationEvent("plan-a", null, MaintenanceOperationEventKind.OperationStatusChanged,
                DateTime.UtcNow, "开始执行", MaintenanceOperationStatus.Running, null),
            new MaintenanceOperationEvent("plan-a", "plan-a:step-a", MaintenanceOperationEventKind.StepStatusChanged,
                DateTime.UtcNow, null, null, MaintenanceStepStatus.Running),
            new MaintenanceOperationEvent("plan-a", "plan-a:step-a", MaintenanceOperationEventKind.StepStatusChanged,
                DateTime.UtcNow, null, null, MaintenanceStepStatus.Succeeded),
            new MaintenanceOperationEvent("plan-a", "other-plan:step-a", MaintenanceOperationEventKind.StepStatusChanged,
                DateTime.UtcNow, null, null, MaintenanceStepStatus.Failed),
            new MaintenanceOperationEvent("plan-a", null, MaintenanceOperationEventKind.OperationStatusChanged,
                DateTime.UtcNow, "执行成功", MaintenanceOperationStatus.Succeeded, null)
        ], () => repository.Items.Add(Operation("plan-a", "device-a", MaintenanceOperationStatus.Succeeded,
            Step("plan-a", "plan-a:step-a", MaintenanceStepStatus.Succeeded, exitCode: 0, duration: TimeSpan.FromSeconds(2)))));
        using var viewModel = CreateViewModel(environment.Paths, Plan(MaintenanceRiskLevel.ReadOnly), executor, repository);
        await viewModel.Initialization;
        viewModel.PlanAcknowledged = true;

        await viewModel.ExecuteAsync();

        Assert.Equal(MaintenanceOperationStatus.Succeeded, viewModel.ExecutionStatus);
        Assert.Equal("成功", Assert.Single(viewModel.PlanSteps).StatusText);
        Assert.Equal("执行成功", viewModel.StatusMessage);
        Assert.Equal("plan-a", Assert.Single(viewModel.History).OperationId);
        Assert.True(repository.ListRecentCallCount >= 2);
    }

    [Fact]
    public async Task OutputReadingRejectsAbsoluteAndTraversalPathsBeforeReading()
    {
        using var environment = TestEnvironment.Create();
        var operationDirectory = Path.Combine(environment.Paths.OperationsDirectory, "op-a");
        Directory.CreateDirectory(operationDirectory);
        await File.WriteAllTextAsync(Path.Combine(operationDirectory, "stdout.log"), "安全输出");
        var outsideFile = Path.Combine(environment.Root, "outside.txt");
        await File.WriteAllTextAsync(outsideFile, "不得读取");
        var repository = new FakeOperationRepository(Operation("op-a", "device-a", MaintenanceOperationStatus.Succeeded,
            Step("op-a", "step-a", MaintenanceStepStatus.Succeeded, stdoutPath: Path.Combine("Operations", "op-a", "stdout.log"))));
        using var viewModel = CreateViewModel(environment.Paths, repository: repository);
        await viewModel.Initialization;
        var step = Assert.Single(Assert.Single(viewModel.History).Steps);

        await viewModel.LoadStdoutAsync(step);
        Assert.Equal("安全输出", viewModel.OutputText);

        step.StdoutPath = outsideFile;
        await viewModel.LoadStdoutAsync(step);
        Assert.Equal("", viewModel.OutputText);
        Assert.Contains("绝对路径", viewModel.ErrorMessage);

        step.StdoutPath = Path.Combine("..", "outside.txt");
        await viewModel.LoadStdoutAsync(step);
        Assert.Equal("", viewModel.OutputText);
        Assert.Contains("越界", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task CancelPendingOperationsCancelsUiWaitAndLeavesExecutorResponsibleForStepSemantics()
    {
        using var environment = TestEnvironment.Create();
        var executor = new WaitingExecutor();
        using var viewModel = CreateViewModel(environment.Paths, Plan(MaintenanceRiskLevel.High), executor);
        await viewModel.Initialization;
        viewModel.PlanAcknowledged = true;
        viewModel.ConfirmationText = "/dev/sda";
        var execution = viewModel.ExecuteAsync();
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        viewModel.CancelPendingOperations();
        await execution.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(executor.CancellationObserved);
        Assert.False(viewModel.IsExecuting);
        Assert.Contains("已取消窗口等待", viewModel.StatusMessage);
    }

    private static MaintenanceWorkspaceViewModel CreateViewModel(
        DataPaths paths,
        ExecutionPlan? plan = null,
        IMaintenanceExecutor? executor = null,
        IMaintenanceOperationRepository? repository = null,
        Action<string>? clipboardWriter = null) =>
        new("device-a", plan, executor ?? new RecordingExecutor([]), repository ?? new FakeOperationRepository(), paths, clipboardWriter ?? (_ => { }));

    private static ExecutionPlan Plan(MaintenanceRiskLevel riskLevel, params ExecutionStep[] steps) => new()
    {
        Id = "plan-a",
        WorkflowId = "test-workflow",
        WorkflowVersion = "1.0.0",
        ExtensionId = "maintenance-tests",
        ExtensionVersion = "2.4.0",
        DeviceId = "device-a",
        TargetType = "linux-open-ssh",
        RiskLevel = riskLevel,
        Target = new StableMaintenanceTarget("block-device", "/dev/sda", "uuid:test"),
        RequiresDeviceNameConfirmation = riskLevel == MaintenanceRiskLevel.High,
        CreatedAt = new DateTime(2026, 8, 24, 1, 2, 3, DateTimeKind.Utc),
        Steps = steps.Length == 0 ? [new ExecutionStep("step-a", 0, "读取设备", "lsblk", ["--json"], true)] : steps
    };

    private static MaintenanceOperation Operation(
        string id,
        string deviceId,
        MaintenanceOperationStatus status,
        params MaintenanceOperationStep[] steps) => new()
    {
        Id = id,
        WorkflowId = "test-workflow",
        WorkflowVersion = "1.0.0",
        ExtensionId = "maintenance-tests",
        ExtensionVersion = "2.4.0",
        DeviceId = deviceId,
        Status = status,
        StartedAt = new DateTime(2026, 8, 24, 1, 2, 3, DateTimeKind.Utc),
        CompletedAt = new DateTime(2026, 8, 24, 1, 2, 6, DateTimeKind.Utc),
        OutcomeSummary = status == MaintenanceOperationStatus.Failed ? "测试失败" : "测试完成",
        OperationDirectory = id,
        Steps = steps
    };

    private static MaintenanceOperationStep Step(
        string operationId,
        string id,
        MaintenanceStepStatus status,
        int? exitCode = null,
        TimeSpan? duration = null,
        string? stdoutPath = null) => new()
    {
        Id = id,
        OperationId = operationId,
        Index = 0,
        Name = "读取设备",
        Status = status,
        Executable = "lsblk",
        Arguments = ["--json"],
        StdoutPath = stdoutPath,
        ExitCode = exitCode,
        Duration = duration
    };

    private sealed class FakeOperationRepository(params MaintenanceOperation[] operations) : IMaintenanceOperationRepository
    {
        public List<MaintenanceOperation> Items { get; } = [.. operations];
        public int ListRecentCallCount { get; private set; }

        public Task CreateAsync(MaintenanceOperation operation, bool isReadOnly, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MaintenanceOperation?> GetAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Items.FirstOrDefault(item => item.Id == id));
        public Task<IReadOnlyList<MaintenanceOperation>> ListRecentAsync(int limit, CancellationToken cancellationToken = default)
        {
            ListRecentCallCount++;
            return Task.FromResult<IReadOnlyList<MaintenanceOperation>>(Items.Take(limit).ToArray());
        }
        public Task UpdateOperationAsync(string id, MaintenanceOperationStatus status, DateTime? completedAt, string? outcomeSummary, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateStepAsync(MaintenanceOperationStepUpdate update, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> HasActiveOperationAsync(string deviceId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<int> RecoverInterruptedAsync(DateTime recoveredAt, CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private sealed class RecordingExecutor(
        IReadOnlyList<MaintenanceOperationEvent> events,
        Action? onCompleted = null) : IMaintenanceExecutor
    {
        public List<MaintenanceExecutionRequest> Requests { get; } = [];

        public async IAsyncEnumerable<MaintenanceOperationEvent> ExecuteAsync(
            MaintenanceExecutionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            foreach (var item in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
                await Task.Yield();
            }
            onCompleted?.Invoke();
        }
    }

    private sealed class WaitingExecutor : IMaintenanceExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CancellationObserved { get; private set; }

        public async IAsyncEnumerable<MaintenanceOperationEvent> ExecuteAsync(
            MaintenanceExecutionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                yield break;
            }
            yield break;
        }
    }

    private sealed class TestEnvironment : IDisposable
    {
        private TestEnvironment(string root)
        {
            Root = root;
            Paths = new DataPaths(root);
            Paths.EnsureCreated();
        }

        public string Root { get; }
        public DataPaths Paths { get; }

        public static TestEnvironment Create() => new(Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N")));

        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch { }
        }
    }

}
