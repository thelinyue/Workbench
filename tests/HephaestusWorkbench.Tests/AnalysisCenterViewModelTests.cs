using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using HephaestusWorkbench.App.ViewModels;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Repositories;
using HephaestusWorkbench.Core.Services;
using HephaestusWorkbench.Data;
using HephaestusWorkbench.PluginSDK;
using HephaestusWorkbench.Services;
using AnalysisTaskStatus = HephaestusWorkbench.Core.Models.TaskStatus;

namespace HephaestusWorkbench.Tests;

public sealed class AnalysisCenterViewModelTests
{
    [Fact]
    public async Task LoadAsync_GroupsInboxCasesTasksAndReportsBySourcePath()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var source = Path.Combine(environment.Paths.InboxDirectory, "diag_DEVICE01_2608111530.tgz");
        await WriteValidArchiveAsync(source);
        await environment.Inbox.StartAsync();
        var extractPath = Path.Combine(environment.Paths.InboxDirectory, "diag_DEVICE01_2608111530");
        var older = CreateCase("case-old", source, extractPath, CaseStatus.Failed, DateTime.Now.AddMinutes(-2));
        var current = CreateCase("case-current", source, extractPath, CaseStatus.Completed, DateTime.Now.AddMinutes(-1));
        await environment.Cases.InsertAsync(older);
        await environment.Cases.InsertAsync(current);
        await environment.Tasks.InsertAsync(CreateTask("task-old", older.Id, AnalysisTaskStatus.Failed, "旧任务失败"));
        await environment.Tasks.InsertAsync(CreateTask("task-current", current.Id, AnalysisTaskStatus.Completed));
        var reportPath = environment.Paths.GetReportDirectory(extractPath);
        Directory.CreateDirectory(reportPath);
        await File.WriteAllTextAsync(Path.Combine(reportPath, "index.html"), "<html>ok</html>");
        await environment.Reports.InsertAsync(new Report { Id = "report-current", CaseId = current.Id, Path = reportPath, PluginId = "test-plugin", CreateTime = DateTime.Now });

        using var center = environment.CreateAnalysisCenter();
        await center.InitializeAsync();

        var group = Assert.Single(center.Items);
        Assert.Equal(Path.GetFullPath(source), group.SourcePath);
        Assert.Equal(2, group.Attempts.Count);
        Assert.Equal("completed", group.StageKey);
        Assert.Equal("打开报告", group.PrimaryActionText);
        Assert.Equal("case-current", group.CurrentAttempt?.Case.Id);

        Assert.Single(center.Items);
    }

    [Fact]
    public async Task LoadAsync_ExposesEveryAnalysisAttemptAsOneHistoryRow()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var source = Path.Combine(environment.Root, "diag_DEVICE01_2608111530.tgz");
        var older = CreateCase("case-old", source, Path.Combine(environment.Root, "extract-old"), CaseStatus.Failed, DateTime.Now.AddMinutes(-2));
        var newer = CreateCase("case-new", source, Path.Combine(environment.Root, "extract-new"), CaseStatus.Completed, DateTime.Now.AddMinutes(-1));
        await environment.Cases.InsertAsync(older);
        await environment.Cases.InsertAsync(newer);
        await environment.Tasks.InsertAsync(CreateTask("task-old", older.Id, AnalysisTaskStatus.Failed));
        await environment.Tasks.InsertAsync(CreateTask("task-new", newer.Id, AnalysisTaskStatus.Completed));

        using var center = environment.CreateAnalysisCenter();
        await center.InitializeAsync();

        Assert.Single(center.Items);
        Assert.Equal(2, center.HistoryItems.Count());
    }

    [Fact]
    public async Task AnalyzeAllPendingCommand_SubmitsAllPendingLogs()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var first = Path.Combine(environment.Paths.InboxDirectory, "diag_DEVICE01_2608111530.tgz");
        var second = Path.Combine(environment.Paths.InboxDirectory, "diag_DEVICE02_2608111531.tgz");
        await WriteValidArchiveAsync(first);
        await WriteValidArchiveAsync(second);
        await environment.Inbox.StartAsync();

        using var center = environment.CreateAnalysisCenter();
        await center.InitializeAsync();
        Assert.Equal(2, center.BulkEligibleCount);
        center.AnalyzeAllPendingCommand.Execute(null);
        await WaitUntilAsync(async () =>
        {
            var tasks = await environment.Tasks.ListAsync();
            return (await environment.Cases.ListAsync()).Count == 2
                && tasks.Count == 2
                && tasks.All(x => x.Status is not AnalysisTaskStatus.Waiting and not AnalysisTaskStatus.Running)
                && !center.IsBulkOperationActive
                && !string.IsNullOrWhiteSpace(center.Message);
        });

        var created = await environment.Cases.ListAsync();
        Assert.Equal(2, created.Count);
        Assert.Contains(created, item => string.Equals(Path.GetFullPath(first), item.SourcePath, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(created, item => string.Equals(Path.GetFullPath(second), item.SourcePath, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("成功 2 个", center.Message);
        Assert.Empty(environment.ReportLauncher.OpenedPaths);
    }

    [Fact]
    public async Task DeleteInvalidCommand_DeletesAllInvalidLifecycle()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var invalid = Path.Combine(environment.Paths.InboxDirectory, "diag_BAD01_2608111530.tgz");
        var otherInvalid = Path.Combine(environment.Paths.InboxDirectory, "diag_BAD02_2608111531.tgz");
        await File.WriteAllTextAsync(invalid, "不是压缩包");
        await File.WriteAllTextAsync(otherInvalid, "也不是压缩包");
        await environment.Inbox.StartAsync();

        var extract = Path.Combine(environment.Paths.InboxDirectory, "diag_BAD01_2608111530");
        Directory.CreateDirectory(extract);
        var residualCase = CreateCase("case-invalid", invalid, extract, CaseStatus.Failed, DateTime.Now);
        await environment.Cases.InsertAsync(residualCase);
        var reportPath = environment.Paths.GetReportDirectory(residualCase.ExtractPath);
        Directory.CreateDirectory(reportPath);
        await File.WriteAllTextAsync(Path.Combine(reportPath, "index.html"), "<html>old</html>");
        await environment.Reports.InsertAsync(new Report { Id = "report-invalid", CaseId = residualCase.Id, Path = reportPath, PluginId = "test-plugin", CreateTime = DateTime.Now });

        using var center = environment.CreateAnalysisCenter();
        await center.InitializeAsync();
        Assert.Equal(2, center.InvalidDeleteCount);
        center.DeleteInvalidCommand.Execute(null);
        await WaitUntilAsync(() => Task.FromResult(!File.Exists(invalid)
            && !File.Exists(otherInvalid)
            && !center.IsBulkOperationActive
            && !string.IsNullOrWhiteSpace(center.Message)));

        Assert.False(Directory.Exists(extract));
        Assert.False(Directory.Exists(reportPath));
        Assert.Null(await environment.Cases.GetAsync(residualCase.Id));
        Assert.False(File.Exists(otherInvalid));
        Assert.Contains("成功 2 个", center.Message);
        Assert.Empty(environment.ReportLauncher.OpenedPaths);
    }

    [Fact]
    public async Task DeleteInvalidCommand_WhenConfirmationIsCancelled_DoesNothing()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var invalid = Path.Combine(environment.Paths.InboxDirectory, "diag_BAD01_2608111530.tgz");
        await File.WriteAllTextAsync(invalid, "不是压缩包");
        await environment.Inbox.StartAsync();

        using var center = environment.CreateAnalysisCenter(confirmDeleteLifecycle: _ => false);
        await center.InitializeAsync();
        center.DeleteInvalidCommand.Execute(null);
        await Task.Delay(50);

        Assert.True(File.Exists(invalid));
        Assert.Equal(1, center.InvalidDeleteCount);
    }

    [Fact]
    public async Task DeleteInvalidCommand_WhenOneFileIsLocked_ContinuesAndReportsFailure()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var locked = Path.Combine(environment.Paths.InboxDirectory, "diag_BAD01_2608111530.tgz");
        var deletable = Path.Combine(environment.Paths.InboxDirectory, "diag_BAD02_2608111531.tgz");
        await File.WriteAllTextAsync(locked, "不是压缩包");
        await File.WriteAllTextAsync(deletable, "也不是压缩包");
        await environment.Inbox.StartAsync();
        await using var lockStream = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read);

        using var center = environment.CreateAnalysisCenter();
        await center.InitializeAsync();
        center.DeleteInvalidCommand.Execute(null);
        await WaitUntilAsync(() => Task.FromResult(!File.Exists(deletable)
            && !center.IsBulkOperationActive
            && center.Message.Contains("失败 1 个", StringComparison.Ordinal)));

        Assert.True(File.Exists(locked));
        Assert.Contains("成功 1 个", center.Message);
        Assert.Contains("失败 1 个", center.Message);
    }

    [Fact]
    public async Task OpenRowReportCommand_OpensLatestReportInDefaultBrowser()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var source = Path.Combine(environment.Root, "diag_DEVICE01_2608111530.tgz");
        var reportCase = CreateCase("case-report", source, Path.Combine(environment.Root, "diag_DEVICE01_2608111530"), CaseStatus.Completed, DateTime.Now);
        await environment.Cases.InsertAsync(reportCase);
        var reportPath = environment.Paths.GetReportDirectory(reportCase.ExtractPath);
        Directory.CreateDirectory(reportPath);
        await File.WriteAllTextAsync(Path.Combine(reportPath, "index.html"), "<html>ok</html>");
        await environment.Reports.InsertAsync(new Report { Id = "report-row", CaseId = reportCase.Id, Path = reportPath, PluginId = "test-plugin", CreateTime = DateTime.Now });

        using var center = environment.CreateAnalysisCenter();
        await center.InitializeAsync();
        var row = Assert.Single(center.Items);

        center.OpenRowReportCommand.Execute(row);
        await WaitUntilAsync(() => Task.FromResult(environment.ReportLauncher.OpenedPaths.Count == 1));

        Assert.Equal(Path.Combine(reportPath, "index.html"), Assert.Single(environment.ReportLauncher.OpenedPaths));
    }

    [Fact]
    public async Task OpenRowReportCommand_WhenOpenSucceedsWithWarning_ShowsChineseWarning()
    {
        const string warning = "报告已打开，但无法记录最后打开时间。";
        await using var environment = await TestEnvironment.CreateAsync();
        var source = Path.Combine(environment.Root, "diag_DEVICE01_2608111530.tgz");
        var reportCase = CreateCase("case-report-warning", source, Path.Combine(environment.Root, "extract-warning"), CaseStatus.Completed, DateTime.Now);
        await environment.Cases.InsertAsync(reportCase);
        var reportPath = environment.Paths.GetReportDirectory(reportCase.ExtractPath);
        Directory.CreateDirectory(reportPath);
        await File.WriteAllTextAsync(Path.Combine(reportPath, "index.html"), "<html>ok</html>");
        await environment.Reports.InsertAsync(new Report { Id = "report-row-warning", CaseId = reportCase.Id, Path = reportPath, PluginId = "test-plugin", CreateTime = DateTime.Now });
        var reportOpenService = new FixedReportOpenService(new ReportOpenResult(true, Path.Combine(reportPath, "index.html"), warning));

        using var center = environment.CreateAnalysisCenter(reportOpenService: reportOpenService);
        await center.InitializeAsync();
        center.OpenRowReportCommand.Execute(Assert.Single(center.Items));
        await WaitUntilAsync(() => Task.FromResult(center.Message.Contains(warning, StringComparison.Ordinal)));

        Assert.Equal(warning, center.Message);
    }

    [Fact]
    public async Task OpenCaseReportAsync_WhenOpenSucceedsWithWarning_ShowsChineseWarning()
    {
        const string warning = "报告已打开，但无法记录最后打开时间。";
        await using var environment = await TestEnvironment.CreateAsync();
        var source = Path.Combine(environment.Root, "diag_DEVICE01_2608111530.tgz");
        var reportCase = CreateCase("case-open-warning", source, Path.Combine(environment.Root, "extract-open-warning"), CaseStatus.Completed, DateTime.Now);
        await environment.Cases.InsertAsync(reportCase);
        var reportPath = environment.Paths.GetReportDirectory(reportCase.ExtractPath);
        Directory.CreateDirectory(reportPath);
        await File.WriteAllTextAsync(Path.Combine(reportPath, "index.html"), "<html>ok</html>");
        await environment.Reports.InsertAsync(new Report { Id = "report-case-warning", CaseId = reportCase.Id, Path = reportPath, PluginId = "test-plugin", CreateTime = DateTime.Now });
        var reportOpenService = new FixedReportOpenService(new ReportOpenResult(true, Path.Combine(reportPath, "index.html"), warning));

        using var center = environment.CreateAnalysisCenter(reportOpenService: reportOpenService);
        Assert.True(await center.OpenCaseReportAsync(reportCase.Id));

        Assert.Equal(warning, center.Message);
    }

    [Fact]
    public async Task MonitoredRowSingleAnalysis_DoesNotOpenBrowserAutomatically()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var source = Path.Combine(environment.Paths.InboxDirectory, "diag_DEVICE01_2608111530.tgz");
        await WriteValidArchiveAsync(source);
        await environment.Inbox.StartAsync();

        using var center = environment.CreateAnalysisCenter();
        await center.InitializeAsync();
        center.AnalyzeSingleCommand.Execute(Assert.Single(center.Items));

        await WaitUntilAsync(async () =>
        {
            var tasks = await environment.Tasks.ListAsync();
            return tasks.Count == 1
                && tasks[0].Status is not AnalysisTaskStatus.Waiting and not AnalysisTaskStatus.Running
                && !center.IsBulkOperationActive;
        });

        Assert.Empty(environment.ReportLauncher.OpenedPaths);
    }

    [Fact]
    public async Task QuickSingleAnalysis_WhenOpenSucceedsWithWarning_ShowsChineseWarning()
    {
        const string warning = "报告已打开，但无法记录最后打开时间。";
        await using var environment = await TestEnvironment.CreateAsync();
        var source = Path.Combine(environment.Paths.InboxDirectory, "diag_DEVICE01_2608111530.tgz");
        await WriteValidArchiveAsync(source);
        await environment.Inbox.StartAsync();
        var reportOpenService = new FixedReportOpenService(new ReportOpenResult(true, "index.html", warning));

        using var center = environment.CreateAnalysisCenter(reportOpenService: reportOpenService);
        await center.InitializeAsync();
        await center.AnalyzeFileAsync(source);

        Assert.Contains(warning, center.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuickSingleAnalysis_OpensNewReportInDefaultBrowser()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var source = Path.Combine(environment.Paths.InboxDirectory, "diag_DEVICE01_2608111530.tgz");
        await WriteValidArchiveAsync(source);
        await environment.Inbox.StartAsync();

        var oldCase = CreateCase("case-old-report", source, Path.Combine(environment.Root, "diag_DEVICE01_2608111530"), CaseStatus.Completed, DateTime.Now.AddMinutes(-1));
        await environment.Cases.InsertAsync(oldCase);
        await environment.Tasks.InsertAsync(CreateTask("task-old-report", oldCase.Id, AnalysisTaskStatus.Completed));
        var oldReportPath = environment.Paths.GetReportDirectory(oldCase.ExtractPath);
        Directory.CreateDirectory(oldReportPath);
        await File.WriteAllTextAsync(Path.Combine(oldReportPath, "index.html"), "<html>old-report</html>");
        await environment.Reports.InsertAsync(new Report
        {
            Id = "report-old",
            CaseId = oldCase.Id,
            Path = oldReportPath,
            PluginId = "test-plugin",
            CreateTime = DateTime.Now.AddMinutes(-1)
        });

        using var center = environment.CreateAnalysisCenter();
        await center.InitializeAsync();
        await center.AnalyzeFileAsync(source);

        await WaitUntilAsync(async () =>
        {
            var cases = await environment.Cases.ListAsync();
            var reports = await environment.Reports.ListAsync(new ReportQuery());
            return cases.Count == 2
                && reports.Count == 2
                && !center.IsBulkOperationActive;
        });

        var latest = (await environment.Reports.ListAsync(new ReportQuery())).OrderByDescending(x => x.CreateTime).First();
        Assert.NotEqual("report-old", latest.Id);
        Assert.Equal("<html>fixture</html>", await File.ReadAllTextAsync(latest.ReportFile));
        Assert.Equal(latest.ReportFile, Assert.Single(environment.ReportLauncher.OpenedPaths));
    }


    [Fact]
    public async Task DeleteInvalidCommand_ConfirmationDoesNotMentionRemovedReportSessions()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var invalid = Path.Combine(environment.Paths.InboxDirectory, "diag_BAD01_2608111530.tgz");
        await File.WriteAllTextAsync(invalid, "不是压缩包");
        await environment.Inbox.StartAsync();
        string? confirmation = null;

        using var center = environment.CreateAnalysisCenter(message =>
        {
            confirmation = message;
            return false;
        });
        await center.InitializeAsync();
        center.DeleteInvalidCommand.Execute(null);

        Assert.NotNull(confirmation);
        Assert.DoesNotContain("报告会话", confirmation, StringComparison.Ordinal);
        Assert.Contains("案例、任务、报告", confirmation, StringComparison.Ordinal);
    }

    private static AnalysisCase CreateCase(string id, string source, string extractPath, CaseStatus status, DateTime updateTime) => new()
    {
        Id = id,
        DisplayName = id,
        OriginalName = Path.GetFileName(source),
        DeviceId = "DEVICE01",
        LogTime = updateTime,
        Status = status,
        SourcePath = source,
        ExtractPath = extractPath,
        ReportPath = status == CaseStatus.Completed ? "report" : null,
        ErrorMessage = status == CaseStatus.Failed ? "分析失败" : null,
        CreateTime = updateTime,
        UpdateTime = updateTime
    };

    private static AnalysisTask CreateTask(string id, string caseId, AnalysisTaskStatus status, string? error = null) => new()
    {
        Id = id,
        CaseId = caseId,
        PluginId = "test-plugin",
        Status = status,
        StartTime = DateTime.Now.AddMinutes(-1),
        EndTime = DateTime.Now,
        ErrorMessage = error
    };

    private static async Task WriteValidArchiveAsync(string path)
    {
        await using var file = File.Create(path);
        await using var gzip = new GZipStream(file, CompressionLevel.Optimal, leaveOpen: false);
        using var tar = new TarWriter(gzip, leaveOpen: false);
        tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "log.txt")
        {
            DataStream = new MemoryStream(Encoding.UTF8.GetBytes("test"))
        });
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < timeout)
        {
            if (await condition()) return;
            await Task.Delay(25);
        }
        Assert.Fail("等待异步操作完成超时。");
    }

    private sealed class TestEnvironment : IAsyncDisposable
    {
        private TestEnvironment(string root, DataPaths paths, SqliteCaseRepository cases, SqliteTaskRepository tasks, SqliteReportRepository reports, LogInboxService inbox, CaseAnalysisService analysis, WorkbenchLogger logger, RecordingReportProcessLauncher reportLauncher)
        {
            Root = root;
            Paths = paths;
            Cases = cases;
            Tasks = tasks;
            Reports = reports;
            Inbox = inbox;
            Analysis = analysis;
            Logger = logger;
            ReportLauncher = reportLauncher;
        }

        public string Root { get; }
        public DataPaths Paths { get; }
        public SqliteCaseRepository Cases { get; }
        public SqliteTaskRepository Tasks { get; }
        public SqliteReportRepository Reports { get; }
        public LogInboxService Inbox { get; }
        public CaseAnalysisService Analysis { get; }
        public WorkbenchLogger Logger { get; }
        public RecordingReportProcessLauncher ReportLauncher { get; }

        public static async Task<TestEnvironment> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
            var paths = new DataPaths(root);
            var factory = new SqliteConnectionFactory(paths);
            await new DatabaseInitializer(factory).InitializeAsync();
            var cases = new SqliteCaseRepository(factory);
            var tasks = new SqliteTaskRepository(factory);
            var reports = new SqliteReportRepository(factory);
            var logger = new WorkbenchLogger(root);
            var registry = await AnalysisExtensionTestSupport.CreateRegistryAsync(paths, AnalysisExtensionTestSupport.Process("test-plugin"));
            var analysis = new CaseAnalysisService(paths, cases, tasks, reports, registry, new ExtensionSettingsStore(paths), new AnalysisProcessHost(logger), new TaskCenter(tasks), logger, new RuleSetService(paths, logger), new SqliteAnalysisLifecycleRepository(factory));
            var configuration = new WorkbenchConfigurationService(paths);
            var inbox = new LogInboxService(new LogFileParser(), new ArchiveValidator(), configuration, logger, paths.InboxDirectory);
            return new TestEnvironment(root, paths, cases, tasks, reports, inbox, analysis, logger, new RecordingReportProcessLauncher());
        }

        public AnalysisCenterViewModel CreateAnalysisCenter(
            Func<string, bool>? confirmDeleteLifecycle = null,
            IReportOpenService? reportOpenService = null)
        {
            var reportService = new ReportService(Reports, Analysis);
            reportOpenService ??= new ReportOpenService(Cases, Reports, ReportLauncher, Logger);
            return new AnalysisCenterViewModel(Inbox, Analysis, reportService, _ => { }, Logger, confirmDeleteLifecycle ?? (_ => true), reportOpenService);
        }

        public ValueTask DisposeAsync()
        {
            Inbox.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedReportOpenService(ReportOpenResult result) : IReportOpenService
    {
        public Task<ReportOpenResult> OpenAsync(ReportOpenRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(result);
    }

    private sealed class RecordingReportProcessLauncher : IReportProcessLauncher
    {
        public List<string> OpenedPaths { get; } = new();
        public void Open(string reportEntryPath) => OpenedPaths.Add(reportEntryPath);
    }
}
