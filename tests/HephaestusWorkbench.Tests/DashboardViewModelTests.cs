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

public sealed class DashboardViewModelTests
{
    [Fact]
    public void HomeLogItem_MapsAnalyzeRunningCompletedAndRetryActions()
    {
        var item = InboxItem("diag_DEVICE01_2608111530.tgz");
        var runningCase = AnalysisCase("case-running", item, CaseStatus.Running);
        var completedCase = AnalysisCase("case-completed", item, CaseStatus.Completed, "report");
        var failedCase = AnalysisCase("case-failed", item, CaseStatus.Failed, error: "插件执行失败");

        Directory.CreateDirectory(completedCase.ExtractPath);
        string? openedExtractPath = null;
        Action<string> openExtractDirectory = path => openedExtractPath = path;
        var fresh = new HomeLogItemViewModel(item, null, null, _ => Task.CompletedTask, openExtractDirectory);
        var running = new HomeLogItemViewModel(item, runningCase, AnalysisTask("task-running", runningCase.Id, AnalysisTaskStatus.Running), _ => Task.CompletedTask, openExtractDirectory);
        var completed = new HomeLogItemViewModel(item, completedCase, AnalysisTask("task-completed", completedCase.Id, AnalysisTaskStatus.Completed), _ => Task.CompletedTask, openExtractDirectory);
        var failed = new HomeLogItemViewModel(item, failedCase, AnalysisTask("task-failed", failedCase.Id, AnalysisTaskStatus.Failed, "规则执行失败"), _ => Task.CompletedTask, openExtractDirectory);

        Assert.Equal("分析并查看", fresh.ActionText);
        Assert.True(fresh.CanAnalyze);
        Assert.Equal("分析中", running.ActionText);
        Assert.False(running.CanAnalyze);
        Assert.Equal("查看报告", completed.ActionText);
        Assert.True(completed.CanOpenReport);
        Assert.True(completed.HasExtractDirectory);
        completed.OpenExtractDirectoryCommand.Execute(null);
        Assert.Equal(completedCase.ExtractPath, openedExtractPath);
        Assert.Equal("重新分析", failed.ActionText);
        Assert.Equal("规则执行失败", failed.DetailMessage);
        var caseRoot = Path.GetDirectoryName(item.FilePath)!;
        if (Directory.Exists(caseRoot)) Directory.Delete(caseRoot, recursive: true);
    }

    [Fact]
    public async Task LoadAsync_ShowsOnlyFiveNewestValidLogsAndCountsInvalidLogs()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        for (var index = 0; index < 6; index++)
            await WriteValidArchiveAsync(Path.Combine(environment.Paths.InboxDirectory, $"diag_DEVICE{index}_26081115{index:D2}.tgz"));
        await File.WriteAllTextAsync(Path.Combine(environment.Paths.InboxDirectory, "bad-name.tgz"), "invalid");
        await environment.Inbox.StartAsync();

        using var dashboard = environment.CreateDashboard(_ => Task.FromResult(true));
        await dashboard.LoadAsync();

        Assert.Equal(5, dashboard.RecentLogs.Count);
        Assert.Equal("diag_DEVICE5_2608111505.tgz", dashboard.RecentLogs[0].FileName);
        Assert.DoesNotContain(dashboard.RecentLogs, x => x.FileName == "diag_DEVICE0_2608111500.tgz");
        Assert.Equal(1, dashboard.InvalidInboxCount);
    }

    [Fact]
    public async Task QuickAnalysis_OpensOnlyTrackedTaskReportOnce()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var directory = Path.Combine(environment.Paths.Root, "Downloads");
        Directory.CreateDirectory(directory);
        var backgroundPath = Path.Combine(directory, "diag_BACKGROUND_2608111530.tgz");
        var quickPath = Path.Combine(directory, "diag_QUICK_2608111531.tgz");
        await WriteValidArchiveAsync(backgroundPath);
        await WriteValidArchiveAsync(quickPath);

        var background = await environment.Inbox.InspectFileAsync(backgroundPath);
        Assert.NotNull(background.Item);
        await environment.Analysis.StartAsync(background.Item!);

        var opened = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var openCount = 0;
        using var dashboard = environment.CreateDashboard(caseId =>
        {
            Interlocked.Increment(ref openCount);
            opened.TrySetResult(caseId);
            return Task.FromResult(true);
        });

        await dashboard.AnalyzeSelectedFileAsync(quickPath);
        var openedCaseId = await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);

        var openedCase = await environment.Analysis.GetCaseAsync(openedCaseId);
        Assert.NotNull(openedCase);
        Assert.Equal(Path.GetFullPath(quickPath), openedCase.SourcePath);
        Assert.Equal(1, openCount);
        Assert.False(dashboard.IsQuickAnalysisActive);
        Assert.False(dashboard.QuickStatusIsError);
        Assert.Equal("报告已打开。", dashboard.QuickStatusMessage);
    }

    private static LogInboxItem InboxItem(string fileName) => new()
    {
        FilePath = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"), fileName),
        FileName = fileName,
        DeviceId = "DEVICE01",
        LogTime = new DateTime(2026, 8, 11, 15, 30, 0),
        FileSize = 1024,
        IsValidArchive = true
    };

    private static AnalysisCase AnalysisCase(string id, LogInboxItem item, CaseStatus status, string? reportPath = null, string? error = null) => new()
    {
        Id = id,
        DisplayName = id,
        OriginalName = item.FileName,
        DeviceId = item.DeviceId,
        LogTime = item.LogTime,
        Status = status,
        SourcePath = item.FilePath,
        ExtractPath = Path.Combine(Path.GetDirectoryName(item.FilePath)!, Path.GetFileNameWithoutExtension(item.FileName)),
        ReportPath = reportPath,
        ErrorMessage = error,
        CreateTime = DateTime.Now,
        UpdateTime = DateTime.Now
    };

    private static AnalysisTask AnalysisTask(string id, string caseId, AnalysisTaskStatus status, string? error = null) => new()
    {
        Id = id,
        CaseId = caseId,
        PluginId = "test-plugin",
        Status = status,
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
        private TestEnvironment(
            string root,
            DataPaths paths,
            LogInboxService inbox,
            CaseAnalysisService analysis,
            StorageService storage,
            WorkbenchLogger logger)
        {
            Root = root;
            Paths = paths;
            Inbox = inbox;
            Analysis = analysis;
            Storage = storage;
            Logger = logger;
        }

        public string Root { get; }
        public DataPaths Paths { get; }
        public LogInboxService Inbox { get; }
        public CaseAnalysisService Analysis { get; }
        public StorageService Storage { get; }
        public WorkbenchLogger Logger { get; }

        public static async Task<TestEnvironment> CreateAsync()
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
            var runner = new ReportWritingRunner();
            var analysis = new CaseAnalysisService(
                paths,
                cases,
                tasks,
                reports,
                new TestPluginCatalog(),
                runner,
                runner,
                new TaskCenter(tasks),
                logger);
            var inbox = new LogInboxService(new LogFileParser(), new ArchiveValidator(), new MemorySettingsStore(), logger, paths.InboxDirectory);
            return new TestEnvironment(root, paths, inbox, analysis, new StorageService(paths, cases, logger), logger);
        }

        public DashboardViewModel CreateDashboard(Func<string, Task<bool>> openReport)
            => new(Analysis, Storage, Inbox, () => { }, () => { }, () => { }, openReport, _ => { }, Logger);

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
            => Task.FromResult<PluginManifest?>(pluginId == Manifest.Id ? Manifest : null);
    }

    private sealed class ReportWritingRunner : IPluginRunner
    {
        public async Task<PluginExecutionResult> RunAsync(PluginManifest manifest, PluginExecutionContext context, CancellationToken cancellationToken = default)
        {
            await Task.Delay(30, cancellationToken);
            Directory.CreateDirectory(context.OutputPath);
            await File.WriteAllTextAsync(Path.Combine(context.OutputPath, "report.html"), "<html>ok</html>", cancellationToken);
            return new PluginExecutionResult(0, context.OutputPath, null);
        }
    }

    private sealed class MemorySettingsStore : ISettingsStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult(_values.GetValueOrDefault(key));

        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }
    }
}
