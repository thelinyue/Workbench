using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using HephaestusWorkbench.App.ViewModels;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Repositories;
using HephaestusWorkbench.Data;
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
        var reportPath = environment.Paths.GetCaseReportDirectory(current.Id);
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

        center.Keyword = "case-old";
        Assert.Single(center.Items);
        center.SelectedStatus = center.StatusOptions.First(x => x.Key == "failed");
        Assert.Empty(center.Items);
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

        public static async Task<TestEnvironment> CreateAsync()
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
            var catalog = new PluginCatalog(paths, logger);
            var analysis = new CaseAnalysisService(paths, cases, tasks, reports, catalog, new LegacyLogAnalyzerRunner(logger), new StandardExePluginRunner(logger), new TaskCenter(tasks), logger);
            var inbox = new LogInboxService(new LogFileParser(), new ArchiveValidator(), new MemorySettingsStore(), logger, paths.InboxDirectory);
            return new TestEnvironment(root, paths, cases, tasks, reports, sessions, settingsStore, inbox, analysis, logger);
        }

        public AnalysisCenterViewModel CreateAnalysisCenter()
        {
            var reportService = new ReportService(Reports, Sessions, Analysis);
            var settings = new SettingsService(SettingsStore, Paths.InboxDirectory);
            var workspace = new ReportsWorkspaceViewModel(reportService, settings, _ => { }, Logger, _ => true);
            return new AnalysisCenterViewModel(Inbox, Analysis, reportService, workspace, _ => { }, Logger, _ => true, _ => true);
        }

        public ValueTask DisposeAsync()
        {
            Inbox.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MemorySettingsStore : ISettingsStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(_values.GetValueOrDefault(key));
        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default) { _values[key] = value; return Task.CompletedTask; }
    }
}
