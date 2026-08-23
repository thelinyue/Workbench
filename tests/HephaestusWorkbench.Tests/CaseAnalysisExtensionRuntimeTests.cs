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
            CaseAnalysisService analysis)
        {
            Root = root;
            Cases = cases;
            Tasks = tasks;
            Reports = reports;
            Analysis = analysis;
        }

        public string Root { get; }
        public DataPaths Paths { get; private init; } = null!;
        public ExtensionRegistry Registry { get; private init; } = null!;
        public SqliteCaseRepository Cases { get; }
        public SqliteTaskRepository Tasks { get; }
        public SqliteReportRepository Reports { get; }
        public CaseAnalysisService Analysis { get; }

        public static Task<TestEnvironment> CreateAsync(params AnalysisExtensionDefinition[] extensions)
            => CreateAsync(null, extensions);

        public static async Task<TestEnvironment> CreateAsync(
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
            IAnalysisLifecycleRepository lifecycle = new SqliteAnalysisLifecycleRepository(factory);
            if (decorateLifecycle is not null)
                lifecycle = decorateLifecycle(paths, registry, lifecycle);
            var analysis = new CaseAnalysisService(
                paths,
                cases,
                tasks,
                reports,
                registry,
                new AnalysisProcessHost(logger),
                new TaskCenter(tasks),
                logger,
                new RuleSetService(paths, logger),
                lifecycle);
            return new TestEnvironment(root, cases, tasks, reports, analysis)
            {
                Paths = paths,
                Registry = registry
            };
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
