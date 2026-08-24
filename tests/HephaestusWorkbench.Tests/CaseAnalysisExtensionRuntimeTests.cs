using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Repositories;
using HephaestusWorkbench.Data;
using HephaestusWorkbench.Services;
using AnalysisTaskStatus = HephaestusWorkbench.Core.Models.TaskStatus;

namespace HephaestusWorkbench.Tests;

public sealed class CaseAnalysisExtensionRuntimeTests
{
    [Fact]
    public async Task StartAndWaitAsync_UsesUniqueV2AnalysisEngineAndFixedComprehensiveScope()
    {
        await using var environment = await TestEnvironment.CreateAsync(
            AnalysisExtensionTestSupport.Process());
        var log = Path.Combine(environment.Root, "diag_DEVICE01_2608111530.tgz");
        await File.WriteAllTextAsync(log, "success");

        var task = await environment.Analysis.StartAndWaitAsync(ValidItem(log));

        Assert.NotNull(task);
        Assert.Equal(AnalysisExtensionTestSupport.ExtensionId, task.PluginId);
        Assert.Equal(AnalysisScope.Comprehensive, task.AnalysisScope);
        Assert.Equal(AnalysisTaskStatus.Completed, task.Status);
        var reportDirectory = Path.Combine(
            Path.GetDirectoryName(log)!,
            "diag_DEVICE01_2608111530",
            "Report");
        Assert.Equal(reportDirectory, task.ReportPath);
        Assert.True(File.Exists(Path.Combine(reportDirectory, "index.html")));
        var report = await environment.Reports.GetByCaseIdAsync(task.CaseId);
        Assert.NotNull(report);
        Assert.Equal("测试扩展 log-analyzer", report.PluginName);
        Assert.Equal(AnalysisExtensionTestSupport.Version, report.PluginVersion);
    }

    [Fact]
    public async Task StartAsync_WhenNoAnalysisEngineExists_DoesNotCreateLifecycle()
    {
        await using var environment = await TestEnvironment.CreateAsync(
            AnalysisExtensionTestSupport.Workspace());
        var log = Path.Combine(environment.Root, "test.tgz");
        await File.WriteAllTextAsync(log, "success");

        var task = await environment.Analysis.StartAsync(ValidItem(log));

        Assert.Null(task);
        Assert.Empty(await environment.Cases.ListAsync());
        Assert.Empty(await environment.Tasks.ListAsync());
    }

    [Fact]
    public async Task StartAsync_WhenAnalysisEngineIsDisabled_DoesNotCreateLifecycle()
    {
        await using var environment = await TestEnvironment.CreateAsync(
            AnalysisExtensionTestSupport.Process());
        await environment.ExtensionSettings.SetEnabledAsync(AnalysisExtensionTestSupport.ExtensionId, false);
        var log = Path.Combine(environment.Root, "disabled.tgz");
        await File.WriteAllTextAsync(log, "success");

        var task = await environment.Analysis.StartAsync(ValidItem(log));

        Assert.Null(task);
        Assert.Empty(await environment.Cases.ListAsync());
        Assert.Empty(await environment.Tasks.ListAsync());
    }

    [Fact]
    public async Task StartAsync_WhenEnabledAnalysisEngineRequiresNewerHost_DoesNotCreateOrQueueLifecycle()
    {
        await using var environment = await TestEnvironment.CreateAsync(
            AnalysisExtensionTestSupport.Process(minHostVersion: "3.0.0"));
        var log = Path.Combine(environment.Root, "future-host.tgz");
        await File.WriteAllTextAsync(log, "success");

        var task = await environment.Analysis.StartAsync(ValidItem(log));
        if (task is not null)
            await environment.TaskCenter.WaitForCompletionAsync(task.Id);

        Assert.Null(task);
        Assert.Empty(await environment.Cases.ListAsync());
        Assert.Empty(await environment.Tasks.ListAsync());
        Assert.False(environment.TaskCenter.IsPluginActive(AnalysisExtensionTestSupport.ExtensionId));
    }

    [Fact]
    public async Task StartAsync_WhenInjectedHostVersionIsOlderThanExtensionRequirement_DoesNotCreateLifecycle()
    {
        await using var environment = await TestEnvironment.CreateAsync(
            "1.0.0",
            AnalysisExtensionTestSupport.Process(minHostVersion: "1.1.0"));
        var log = Path.Combine(environment.Root, "injected-host-version.tgz");
        await File.WriteAllTextAsync(log, "success");

        var task = await environment.Analysis.StartAsync(ValidItem(log));

        Assert.Null(task);
        Assert.Empty(await environment.Cases.ListAsync());
        Assert.Empty(await environment.Tasks.ListAsync());
    }

    [Fact]
    public async Task StartAsync_WhenMultipleAnalysisEnginesExist_RejectsAmbiguousSelection()
    {
        await using var environment = await TestEnvironment.CreateAsync(
            AnalysisExtensionTestSupport.Process("first-analyzer"),
            AnalysisExtensionTestSupport.Process("second-analyzer"));
        var log = Path.Combine(environment.Root, "test.tgz");
        await File.WriteAllTextAsync(log, "success");

        var task = await environment.Analysis.StartAsync(ValidItem(log));

        Assert.Null(task);
        Assert.Empty(await environment.Cases.ListAsync());
        Assert.Empty(await environment.Tasks.ListAsync());
    }

    [Fact]
    public async Task StartAsync_WhenDisableIsRequestedDuringLifecycleCreation_DisableWaitsForCreate()
    {
        BlockingCreateLifecycleRepository? lifecycle = null;
        await using var environment = await TestEnvironment.CreateAsync(
            (_, _, inner) => lifecycle = new BlockingCreateLifecycleRepository(inner),
            AnalysisExtensionTestSupport.Process());
        var log = Path.Combine(environment.Root, "disable-race.tgz");
        await File.WriteAllTextAsync(log, "success");

        var start = environment.Analysis.StartAsync(ValidItem(log));
        await lifecycle!.CreateEntered.Task;
        var disable = environment.ExtensionSettings.SetEnabledAsync(
            AnalysisExtensionTestSupport.ExtensionId,
            false);

        Assert.False(disable.IsCompleted);
        lifecycle.AllowCreate.SetResult(null);
        var task = await start;
        await disable;

        Assert.NotNull(task);
        Assert.Single(await environment.Cases.ListAsync());
        Assert.False(Assert.Single((await environment.ExtensionSettings.EnsureAsync()).Extensions).Enabled);
    }

    [Fact]
    public async Task StartAndWaitAsync_WhenCurrentSwitchesAfterSelection_ExecutesSelectedFilteredVersion()
    {
        BlockingCreateLifecycleRepository? lifecycle = null;
        await using var environment = await TestEnvironment.CreateAsync(
            (_, _, inner) => lifecycle = new BlockingCreateLifecycleRepository(inner),
            AnalysisExtensionTestSupport.Process(version: "3.0.0", capabilities: ["workspace.page"]),
            AnalysisExtensionTestSupport.Process(version: "2.0.0"));
        var log = Path.Combine(environment.Root, "version-switch.tgz");
        await File.WriteAllTextAsync(log, "success");

        var analysis = environment.Analysis.StartAndWaitAsync(ValidItem(log));
        await lifecycle!.CreateEntered.Task;
        AnalysisExtensionTestSupport.WriteCurrent(
            environment.Paths,
            AnalysisExtensionTestSupport.ExtensionId,
            "3.0.0");
        await environment.Registry.LoadAsync();
        lifecycle.AllowCreate.SetResult(null);

        var task = await analysis;

        Assert.NotNull(task);
        Assert.Equal(AnalysisTaskStatus.Completed, task.Status);
        var report = await environment.Reports.GetByCaseIdAsync(task.CaseId);
        Assert.NotNull(report);
        Assert.Equal("2.0.0", report.PluginVersion);
    }

    [Fact]
    public async Task TaskCenter_WhenQueuedAnalysisIsCancelled_ReleasesSelectedVersionLease()
    {
        await using var environment = await TestEnvironment.CreateAsync(
            AnalysisExtensionTestSupport.Process(version: "2.1.0"),
            AnalysisExtensionTestSupport.Process(version: "2.0.0"));
        var slotsEntered = new[]
        {
            new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var releaseSlots = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockers = slotsEntered.Select((entered, index) =>
            environment.TaskCenter.EnqueueAsync(
                SlotBlocker($"slot-{index}"),
                async token =>
                {
                    entered.SetResult(null);
                    await releaseSlots.Task.WaitAsync(token);
                })).ToArray();

        try
        {
            await Task.WhenAll(slotsEntered.Select(entered => entered.Task));
            var log = Path.Combine(environment.Root, "queued-cancel.tgz");
            await File.WriteAllTextAsync(log, "success");
            var task = await environment.Analysis.StartAsync(ValidItem(log));
            Assert.NotNull(task);
            var queuedCompletion = environment.TaskCenter.WaitForCompletionAsync(task.Id);

            AnalysisExtensionTestSupport.WriteCurrent(
                environment.Paths,
                AnalysisExtensionTestSupport.ExtensionId,
                "2.1.0");
            await environment.Registry.LoadAsync();
            Assert.False(environment.Registry.CanDeleteVersion(
                AnalysisExtensionTestSupport.ExtensionId,
                "2.0.0"));

            Assert.True(await environment.Analysis.CancelAsync(task.Id));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queuedCompletion);

            Assert.True(environment.Registry.CanDeleteVersion(
                AnalysisExtensionTestSupport.ExtensionId,
                "2.0.0"));
        }
        finally
        {
            releaseSlots.TrySetResult(null);
            await Task.WhenAll(blockers);
        }
    }

    [Fact]
    public async Task StartAsync_WhenLifecycleCreateFails_ReleasesSelectedVersionLease()
    {
        await using var environment = await TestEnvironment.CreateAsync(
            (_, _, inner) => new FailingCreateLifecycleRepository(inner),
            AnalysisExtensionTestSupport.Process(version: "2.1.0"),
            AnalysisExtensionTestSupport.Process(version: "2.0.0"));
        var log = Path.Combine(environment.Root, "create-failure.tgz");
        await File.WriteAllTextAsync(log, "success");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => environment.Analysis.StartAsync(ValidItem(log)));
        Assert.Contains("模拟生命周期创建失败", error.Message);
        AnalysisExtensionTestSupport.WriteCurrent(
            environment.Paths,
            AnalysisExtensionTestSupport.ExtensionId,
            "2.1.0");
        await environment.Registry.LoadAsync();

        Assert.True(environment.Registry.CanDeleteVersion(
            AnalysisExtensionTestSupport.ExtensionId,
            "2.0.0"));
        Assert.Empty(await environment.Cases.ListAsync());
        Assert.Empty(await environment.Tasks.ListAsync());
    }

    [Fact]
    public async Task StartAndWaitAsync_WhenActiveRulesExist_ForwardsHostManagedRulesPath()
    {
        await using var environment = await TestEnvironment.CreateAsync(
            AnalysisExtensionTestSupport.Process());
        var log = Path.Combine(environment.Root, "rules.tgz");
        await File.WriteAllTextAsync(log, "success");
        Directory.CreateDirectory(Path.GetDirectoryName(environment.Paths.ActiveRulesFile)!);
        await File.WriteAllTextAsync(environment.Paths.ActiveRulesFile, """
            {"version":"test","files":[{"name":"messages","category":"系统","keywords":[{"term":"error","result":"错误"}]}]}
            """);

        var task = await environment.Analysis.StartAndWaitAsync(ValidItem(log));

        Assert.NotNull(task);
        var requestJson = await File.ReadAllTextAsync(Path.Combine(environment.Root, "rules", "fixture.request.json"));
        using var request = System.Text.Json.JsonDocument.Parse(requestJson);
        Assert.Equal(environment.Paths.ActiveRulesFile, request.RootElement.GetProperty("rulesPath").GetString());
    }

    [Fact]
    public async Task StartAndWaitAsync_WhenInitialCompletionFails_HoldsLeaseUntilRecoveryStateIsPersisted()
    {
        FailFirstCompletionLifecycleRepository? lifecycle = null;
        await using var environment = await TestEnvironment.CreateAsync(
            (paths, registry, inner) => lifecycle = new FailFirstCompletionLifecycleRepository(
                inner,
                async () =>
                {
                    AnalysisExtensionTestSupport.WriteCurrent(paths, AnalysisExtensionTestSupport.ExtensionId, "2.1.0");
                    await registry.LoadAsync();
                },
                () => !registry.CanDeleteVersion(AnalysisExtensionTestSupport.ExtensionId, "2.0.0")),
            AnalysisExtensionTestSupport.Process(version: "2.1.0"),
            AnalysisExtensionTestSupport.Process(version: "2.0.0"));
        var log = Path.Combine(environment.Root, "persist-failure.tgz");
        await File.WriteAllTextAsync(log, "success");

        var task = await environment.Analysis.StartAndWaitAsync(ValidItem(log));

        Assert.NotNull(task);
        Assert.Equal(AnalysisTaskStatus.Failed, task.Status);
        Assert.True(lifecycle!.LeaseHeldDuringRecoveryPersistence);
    }

    [Fact]
    public async Task RunningTask_HoldsOriginalVersionLeaseUntilCancellationCompletes()
    {
        await using var environment = await TestEnvironment.CreateAsync(
            AnalysisExtensionTestSupport.Process(version: "2.1.0"),
            AnalysisExtensionTestSupport.Process(version: "2.0.0"));
        var log = Path.Combine(environment.Root, "sleep.tgz");
        await File.WriteAllTextAsync(log, "sleep");

        var task = await environment.Analysis.StartAsync(ValidItem(log));
        Assert.NotNull(task);
        var marker = Path.Combine(environment.Root, "sleep", "fixture.started");
        await WaitUntilAsync(() => File.Exists(marker));

        AnalysisExtensionTestSupport.WriteCurrent(environment.Paths, AnalysisExtensionTestSupport.ExtensionId, "2.1.0");
        await environment.Registry.LoadAsync();

        Assert.False(environment.Registry.CanDeleteVersion(AnalysisExtensionTestSupport.ExtensionId, "2.0.0"));
        Assert.True(await environment.Analysis.CancelAsync(task.Id));
        await WaitUntilAsync(() => environment.Registry.CanDeleteVersion(AnalysisExtensionTestSupport.ExtensionId, "2.0.0"));
    }

    private static AnalysisTask SlotBlocker(string id) => new()
    {
        Id = id,
        CaseId = id,
        PluginId = "slot-blocker",
        Status = AnalysisTaskStatus.Waiting
    };

    private static LogInboxItem ValidItem(string path) => new()
    {
        FilePath = path,
        FileName = Path.GetFileName(path),
        DeviceId = "DEVICE01",
        LogTime = DateTime.Now,
        FileSize = new FileInfo(path).Length,
        IsValidArchive = true
    };


    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < timeout)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        Assert.Fail("等待异步分析状态超时。");
    }

    /// <summary>生命周期初始事务失败，用于确认尚未移交给 TaskCenter 的租约由 StartAsync 归还。</summary>
    private sealed class FailingCreateLifecycleRepository(IAnalysisLifecycleRepository inner)
        : IAnalysisLifecycleRepository
    {
        public Task CreateAsync(AnalysisCase analysisCase, AnalysisTask task, CancellationToken cancellationToken = default)
            => Task.FromException(new InvalidOperationException("模拟生命周期创建失败。"));

        public Task MarkRunningAsync(AnalysisCase analysisCase, AnalysisTask task, CancellationToken cancellationToken = default)
            => inner.MarkRunningAsync(analysisCase, task, cancellationToken);

        public Task CompleteAsync(
            AnalysisCase analysisCase,
            AnalysisTask task,
            Report? report,
            CancellationToken cancellationToken = default)
            => inner.CompleteAsync(analysisCase, task, report, cancellationToken);

        public Task DeleteByCaseIdsAsync(IReadOnlyCollection<string> caseIds, CancellationToken cancellationToken = default)
            => inner.DeleteByCaseIdsAsync(caseIds, cancellationToken);

        public Task<int> RecoverInterruptedAsync(DateTime recoveredAt, CancellationToken cancellationToken = default)
            => inner.RecoverInterruptedAsync(recoveredAt, cancellationToken);
    }

    /// <summary>
    /// 在生命周期创建边界暂停分析，精确控制 manifest 选中后、后台执行前的版本切换时序。
    /// </summary>
    private sealed class BlockingCreateLifecycleRepository(IAnalysisLifecycleRepository inner)
        : IAnalysisLifecycleRepository
    {
        public TaskCompletionSource<object?> CreateEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<object?> AllowCreate { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task CreateAsync(
            AnalysisCase analysisCase,
            AnalysisTask task,
            CancellationToken cancellationToken = default)
        {
            CreateEntered.SetResult(null);
            await AllowCreate.Task.WaitAsync(cancellationToken);
            await inner.CreateAsync(analysisCase, task, cancellationToken);
        }

        public Task MarkRunningAsync(AnalysisCase analysisCase, AnalysisTask task, CancellationToken cancellationToken = default)
            => inner.MarkRunningAsync(analysisCase, task, cancellationToken);

        public Task CompleteAsync(
            AnalysisCase analysisCase,
            AnalysisTask task,
            Report? report,
            CancellationToken cancellationToken = default)
            => inner.CompleteAsync(analysisCase, task, report, cancellationToken);

        public Task DeleteByCaseIdsAsync(IReadOnlyCollection<string> caseIds, CancellationToken cancellationToken = default)
            => inner.DeleteByCaseIdsAsync(caseIds, cancellationToken);

        public Task<int> RecoverInterruptedAsync(DateTime recoveredAt, CancellationToken cancellationToken = default)
            => inner.RecoverInterruptedAsync(recoveredAt, cancellationToken);
    }

    /// <summary>
    /// 首次完成事务失败后记录异常补偿落库期间旧版本是否仍受租约保护，
    /// 用于覆盖“进程结束但最终状态尚未落库”的关键生命周期窗口。
    /// </summary>
    private sealed class FailFirstCompletionLifecycleRepository(
        IAnalysisLifecycleRepository inner,
        Func<Task> beforeFirstFailure,
        Func<bool> isOriginalVersionProtected) : IAnalysisLifecycleRepository
    {
        private int _completionAttempts;

        public bool? LeaseHeldDuringRecoveryPersistence { get; private set; }

        public Task CreateAsync(AnalysisCase analysisCase, AnalysisTask task, CancellationToken cancellationToken = default)
            => inner.CreateAsync(analysisCase, task, cancellationToken);

        public Task MarkRunningAsync(AnalysisCase analysisCase, AnalysisTask task, CancellationToken cancellationToken = default)
            => inner.MarkRunningAsync(analysisCase, task, cancellationToken);

        public async Task CompleteAsync(
            AnalysisCase analysisCase,
            AnalysisTask task,
            Report? report,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _completionAttempts) == 1)
            {
                await beforeFirstFailure();
                throw new InvalidOperationException("模拟首次分析结果落库失败。");
            }

            LeaseHeldDuringRecoveryPersistence = isOriginalVersionProtected();
            await inner.CompleteAsync(analysisCase, task, report, cancellationToken);
        }

        public Task DeleteByCaseIdsAsync(IReadOnlyCollection<string> caseIds, CancellationToken cancellationToken = default)
            => inner.DeleteByCaseIdsAsync(caseIds, cancellationToken);

        public Task<int> RecoverInterruptedAsync(DateTime recoveredAt, CancellationToken cancellationToken = default)
            => inner.RecoverInterruptedAsync(recoveredAt, cancellationToken);
    }

    private sealed class TestEnvironment : IAsyncDisposable
    {
        private TestEnvironment(
            string root,
            SqliteCaseRepository cases,
            SqliteTaskRepository tasks,
            SqliteReportRepository reports,
            TaskCenter taskCenter,
            CaseAnalysisService analysis)
        {
            Root = root;
            Cases = cases;
            Tasks = tasks;
            Reports = reports;
            TaskCenter = taskCenter;
            Analysis = analysis;
        }

        public string Root { get; }
        public DataPaths Paths { get; private init; } = null!;
        public ExtensionRegistry Registry { get; private init; } = null!;
        public ExtensionSettingsStore ExtensionSettings { get; private init; } = null!;
        public SqliteCaseRepository Cases { get; }
        public SqliteTaskRepository Tasks { get; }
        public SqliteReportRepository Reports { get; }
        public TaskCenter TaskCenter { get; }
        public CaseAnalysisService Analysis { get; }

        public static Task<TestEnvironment> CreateAsync(params AnalysisExtensionDefinition[] extensions)
            => CreateAsync("2.0.0", null, extensions);

        public static Task<TestEnvironment> CreateAsync(
            string hostVersion,
            params AnalysisExtensionDefinition[] extensions)
            => CreateAsync(hostVersion, null, extensions);

        public static Task<TestEnvironment> CreateAsync(
            Func<DataPaths, ExtensionRegistry, IAnalysisLifecycleRepository, IAnalysisLifecycleRepository>? decorateLifecycle,
            params AnalysisExtensionDefinition[] extensions)
            => CreateAsync("2.0.0", decorateLifecycle, extensions);

        private static async Task<TestEnvironment> CreateAsync(
            string hostVersion,
            Func<DataPaths, ExtensionRegistry, IAnalysisLifecycleRepository, IAnalysisLifecycleRepository>? decorateLifecycle,
            params AnalysisExtensionDefinition[] extensions)
        {
            var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
            var paths = new DataPaths(root);
            paths.EnsureCreated();
            var factory = new SqliteConnectionFactory(paths);
            await new DatabaseInitializer(factory).InitializeAsync();
            var cases = new SqliteCaseRepository(factory);
            var tasks = new SqliteTaskRepository(factory);
            var reports = new SqliteReportRepository(factory);
            var logger = new WorkbenchLogger(root);
            var registry = await AnalysisExtensionTestSupport.CreateRegistryAsync(paths, extensions);
            var extensionSettings = new ExtensionSettingsStore(paths);
            var taskCenter = new TaskCenter(tasks);
            IAnalysisLifecycleRepository lifecycle = new SqliteAnalysisLifecycleRepository(factory);
            if (decorateLifecycle is not null)
                lifecycle = decorateLifecycle(paths, registry, lifecycle);
            var analysis = new CaseAnalysisService(
                paths,
                cases,
                tasks,
                reports,
                registry,
                extensionSettings,
                new AnalysisProcessHost(logger),
                taskCenter,
                logger,
                new RuleSetService(paths, logger),
                lifecycle,
                hostVersion);
            return new TestEnvironment(root, cases, tasks, reports, taskCenter, analysis)
            {
                Paths = paths,
                Registry = registry,
                ExtensionSettings = extensionSettings
            };
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
