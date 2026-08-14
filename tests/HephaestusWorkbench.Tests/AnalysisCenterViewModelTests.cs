using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using HephaestusWorkbench.App.ViewModels;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Repositories;
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
        await File.WriteAllTextAsync(Path.Combine(reportPath, "report.html"), "<html>ok</html>");
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
    public async Task LoadAsync_RefreshesReportLibraryAfterNewReportIsInserted()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var center = environment.CreateAnalysisCenter();
        await center.InitializeAsync();
        Assert.Empty(center.Reports.Library.Items);

        var reportCase = CreateCase(
            "case-new-report",
            Path.Combine(environment.Root, "diag_DEVICE02_2608111600.tgz"),
            Path.Combine(environment.Root, "diag_DEVICE02_2608111600"),
            CaseStatus.Completed,
            DateTime.Now);
        await environment.Cases.InsertAsync(reportCase);
        var reportPath = environment.Paths.GetReportDirectory(reportCase.ExtractPath);
        Directory.CreateDirectory(reportPath);
        await File.WriteAllTextAsync(Path.Combine(reportPath, "report.html"), "<html>ok</html>");
        await environment.Reports.InsertAsync(new Report
        {
            Id = "report-new",
            CaseId = reportCase.Id,
            Path = reportPath,
            PluginId = "test-plugin",
            CreateTime = DateTime.Now
        });

        await center.LoadAsync();

        var report = Assert.Single(center.Reports.Library.Items);
        Assert.Equal("report-new", report.Id);
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
        await File.WriteAllTextAsync(Path.Combine(reportPath, "report.html"), "<html>old</html>");
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
    public async Task OpenRowReportCommand_OpensLatestReportAndDeduplicatesTab()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var source = Path.Combine(environment.Root, "diag_DEVICE01_2608111530.tgz");
        var reportCase = CreateCase("case-report", source, Path.Combine(environment.Root, "diag_DEVICE01_2608111530"), CaseStatus.Completed, DateTime.Now);
        await environment.Cases.InsertAsync(reportCase);
        var reportPath = environment.Paths.GetReportDirectory(reportCase.ExtractPath);
        Directory.CreateDirectory(reportPath);
        await File.WriteAllTextAsync(Path.Combine(reportPath, "report.html"), "<html>ok</html>");
        await environment.Reports.InsertAsync(new Report { Id = "report-row", CaseId = reportCase.Id, Path = reportPath, PluginId = "test-plugin", CreateTime = DateTime.Now });

        using var center = environment.CreateAnalysisCenter();
        await center.InitializeAsync();
        var row = Assert.Single(center.Items);

        center.OpenRowReportCommand.Execute(row);
        center.OpenRowReportCommand.Execute(row);
        await WaitUntilAsync(() => Task.FromResult(center.Reports.OpenTabs.Count == 1));

        Assert.Equal("report-row", Assert.Single(center.Reports.OpenTabs).Report.Id);
    }

    [Fact]
    public async Task AnalyzeSingleCommand_ReanalyzesAndOpensNewReport()
    {
        await using var environment = await TestEnvironment.CreateAsync(new SuccessfulRunner());
        var source = Path.Combine(environment.Paths.InboxDirectory, "diag_DEVICE01_2608111530.tgz");
        await WriteValidArchiveAsync(source);
        await environment.Inbox.StartAsync();

        var oldCase = CreateCase("case-old-report", source, Path.Combine(environment.Root, "diag_DEVICE01_2608111530"), CaseStatus.Completed, DateTime.Now.AddMinutes(-1));
        await environment.Cases.InsertAsync(oldCase);
        await environment.Tasks.InsertAsync(CreateTask("task-old-report", oldCase.Id, AnalysisTaskStatus.Completed));
        var oldReportPath = environment.Paths.GetReportDirectory(oldCase.ExtractPath);
        Directory.CreateDirectory(oldReportPath);
        await File.WriteAllTextAsync(Path.Combine(oldReportPath, "report.html"), "<html>old-report</html>");
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
        center.AnalyzeSingleCommand.Execute(Assert.Single(center.Items));

        await WaitUntilAsync(async () =>
        {
            var cases = await environment.Cases.ListAsync();
            var reports = await environment.Reports.ListAsync(new ReportQuery());
            return cases.Count == 2
                && reports.Count == 2
                && !center.IsBulkOperationActive;
        });

        await center.Reports.Library.LoadAsync();
        var latest = (await environment.Reports.ListAsync(new ReportQuery())).OrderByDescending(x => x.CreateTime).First();
        Assert.NotEqual("report-old", latest.Id);
        Assert.Equal("<html>new-report</html>", await File.ReadAllTextAsync(latest.ReportFile));
        Assert.Single(center.Reports.Library.Items);
        Assert.Equal(latest.Id, center.Reports.Library.Items[0].Id);
        Assert.Equal(latest.Id, center.Reports.OpenTabs.Last().Report.Id);
    }

    [Fact]
    public async Task TaskPanel_ShowsAllActiveAndOnlyTenRecentTasksThenNavigatesToCase()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        for (var index = 0; index < 14; index++)
        {
            var source = Path.Combine(environment.Root, $"diag_DEVICE{index}_26081115{index:D2}.tgz");
            var item = CreateCase($"case-{index}", source, Path.Combine(environment.Root, $"diag_DEVICE{index}_26081115{index:D2}"), CaseStatus.Completed, DateTime.Now.AddMinutes(-index));
            await environment.Cases.InsertAsync(item);
            var status = index < 2 ? (index == 0 ? AnalysisTaskStatus.Running : AnalysisTaskStatus.Waiting) : AnalysisTaskStatus.Completed;
            await environment.Tasks.InsertAsync(new AnalysisTask
            {
                Id = $"task-{index}",
                CaseId = item.Id,
                PluginId = "test-plugin",
                Status = status,
                StartTime = DateTime.Now.AddMinutes(-index - 1),
                EndTime = status == AnalysisTaskStatus.Completed ? DateTime.Now.AddMinutes(-index) : null
            });
        }

        string? openedCase = null;
        using var panel = new TaskPanelViewModel(environment.Analysis, caseId => openedCase = caseId, _ => true);
        await panel.LoadAsync();

        Assert.Equal(2, panel.ActiveTaskCount);
        Assert.Equal(12, panel.Items.Count);
        Assert.All(panel.Items.Take(2), x => Assert.True(x.IsActive));
        panel.IsOpen = true;
        panel.OpenTaskCommand.Execute(panel.Items[0]);
        Assert.Equal(panel.Items[0].Task.CaseId, openedCase);
        Assert.False(panel.IsOpen);
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
        private TestEnvironment(string root, DataPaths paths, SqliteCaseRepository cases, SqliteTaskRepository tasks, SqliteReportRepository reports, SqliteReportSessionRepository sessions, SqliteSettingsStore settingsStore, LogInboxService inbox, CaseAnalysisService analysis, WorkbenchLogger logger)
        {
            Root = root;
            Paths = paths;
            Cases = cases;
            Tasks = tasks;
            Reports = reports;
            Sessions = sessions;
            SettingsStore = settingsStore;
            Inbox = inbox;
            Analysis = analysis;
            Logger = logger;
        }

        public string Root { get; }
        public DataPaths Paths { get; }
        public SqliteCaseRepository Cases { get; }
        public SqliteTaskRepository Tasks { get; }
        public SqliteReportRepository Reports { get; }
        public SqliteReportSessionRepository Sessions { get; }
        public SqliteSettingsStore SettingsStore { get; }
        public LogInboxService Inbox { get; }
        public CaseAnalysisService Analysis { get; }
        public WorkbenchLogger Logger { get; }

        public static async Task<TestEnvironment> CreateAsync(IPluginRunner? runner = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
            var paths = new DataPaths(root);
            var factory = new SqliteConnectionFactory(paths);
            await new DatabaseInitializer(factory).InitializeAsync();
            var cases = new SqliteCaseRepository(factory);
            var tasks = new SqliteTaskRepository(factory);
            var reports = new SqliteReportRepository(factory);
            var sessions = new SqliteReportSessionRepository(factory);
            var settingsStore = new SqliteSettingsStore(factory);
            var logger = new WorkbenchLogger(root);
            runner ??= new FailedRunner();
            var analysis = new CaseAnalysisService(paths, cases, tasks, reports, new TestPluginCatalog(), runner, runner, new TaskCenter(tasks), logger);
            var inbox = new LogInboxService(new LogFileParser(), new ArchiveValidator(), new MemorySettingsStore(), logger, paths.InboxDirectory);
            return new TestEnvironment(root, paths, cases, tasks, reports, sessions, settingsStore, inbox, analysis, logger);
        }

        public AnalysisCenterViewModel CreateAnalysisCenter(Func<string, bool>? confirmDeleteLifecycle = null)
        {
            var reportService = new ReportService(Reports, Sessions, Analysis);
            var settings = new SettingsService(SettingsStore, Paths.InboxDirectory);
            var workspace = new ReportsWorkspaceViewModel(reportService, settings, _ => { }, _ => { }, Logger, _ => true);
            var storage = new StorageService(Paths, Cases, Logger);
            return new AnalysisCenterViewModel(Inbox, Analysis, reportService, storage, settings, workspace, _ => { }, Logger, confirmDeleteLifecycle ?? (_ => true));
        }

        public ValueTask DisposeAsync()
        {
            Inbox.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestPluginCatalog : IPluginCatalog
    {
        private static readonly PluginManifest Manifest = new()
        {
            Id = "test-plugin",
            Name = "测试插件",
            Version = "1.0.0",
            Type = PluginType.Exe,
            Entry = "test.exe"
        };

        public Task<IReadOnlyList<PluginManifest>> ScanAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PluginManifest>>(new[] { Manifest });

        public Task<PluginManifest?> GetAsync(string pluginId, CancellationToken cancellationToken = default)
            => Task.FromResult<PluginManifest?>(string.Equals(pluginId, Manifest.Id, StringComparison.OrdinalIgnoreCase) ? Manifest : null);
    }

    private sealed class FailedRunner : IPluginRunner
    {
        public Task<PluginExecutionResult> RunAsync(PluginManifest manifest, PluginExecutionContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new PluginExecutionResult(1, null, "测试结束"));
    }

    private sealed class SuccessfulRunner : IPluginRunner
    {
        public Task<PluginExecutionResult> RunAsync(PluginManifest manifest, PluginExecutionContext context, CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(context.OutputPath);
            File.WriteAllText(Path.Combine(context.OutputPath, "report.html"), "<html>new-report</html>");
            return Task.FromResult(new PluginExecutionResult(0, context.OutputPath, null));
        }
    }

    private sealed class MemorySettingsStore : ISettingsStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(_values.GetValueOrDefault(key));
        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default) { _values[key] = value; return Task.CompletedTask; }
    }
}
