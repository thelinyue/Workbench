using HephaestusWorkbench.Core.Models;
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

        public static async Task<TestEnvironment> CreateAsync(params AnalysisExtensionDefinition[] extensions)
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
            var analysis = new CaseAnalysisService(
                paths,
                cases,
                tasks,
                reports,
                registry,
                new AnalysisProcessHost(logger),
                new TaskCenter(tasks),
                logger,
                new SqliteAnalysisLifecycleRepository(factory));
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
