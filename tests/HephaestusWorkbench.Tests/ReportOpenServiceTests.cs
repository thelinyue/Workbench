using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Repositories;
using HephaestusWorkbench.Core.Services;
using HephaestusWorkbench.Data;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

public sealed class ReportOpenServiceTests
{
    [Fact]
    public async Task OpenAsync_RejectsReportDirectoryWithTraversalSegments()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var extractPath = environment.CreateExtractDirectory("case-traversal");
        var unsafeReportPath = Path.Combine(extractPath, "Report", "..", "Other");
        Directory.CreateDirectory(unsafeReportPath);
        await File.WriteAllTextAsync(Path.Combine(unsafeReportPath, "index.html"), "<html></html>");
        await environment.InsertAsync("case-traversal", "report-traversal", extractPath, unsafeReportPath);

        var result = await environment.Service.OpenAsync(new ReportOpenRequest("report-traversal"));

        Assert.False(result.Success);
        Assert.Contains("路径", result.ErrorMessage);
        Assert.Empty(environment.Launcher.OpenedPaths);
    }

    [Fact]
    public async Task OpenAsync_RejectsReportOutsideCaseExtractDirectory()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var extractPath = environment.CreateExtractDirectory("case-outside");
        var outsideReportPath = Path.Combine(environment.Root, "Other", "Report");
        Directory.CreateDirectory(outsideReportPath);
        await File.WriteAllTextAsync(Path.Combine(outsideReportPath, "index.html"), "<html></html>");
        await environment.InsertAsync("case-outside", "report-outside", extractPath, outsideReportPath);

        var result = await environment.Service.OpenAsync(new ReportOpenRequest("report-outside"));

        Assert.False(result.Success);
        Assert.Contains("解压目录", result.ErrorMessage);
        Assert.Empty(environment.Launcher.OpenedPaths);
    }

    [Fact]
    public async Task OpenAsync_RejectsMissingIndexHtml()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var extractPath = environment.CreateExtractDirectory("case-missing");
        var reportPath = Path.Combine(extractPath, "Report");
        Directory.CreateDirectory(reportPath);
        await environment.InsertAsync("case-missing", "report-missing", extractPath, reportPath);

        var result = await environment.Service.OpenAsync(new ReportOpenRequest("report-missing"));

        Assert.False(result.Success);
        Assert.Contains("index.html", result.ErrorMessage);
        Assert.Empty(environment.Launcher.OpenedPaths);
    }

    [Fact]
    public async Task OpenAsync_DoesNotUpdateLastOpenedAtWhenBrowserLaunchFails()
    {
        await using var environment = await TestEnvironment.CreateAsync(launchError: new InvalidOperationException("没有默认浏览器"));
        var extractPath = environment.CreateExtractDirectory("case-launch-failure");
        var reportPath = await environment.CreateReportAsync(extractPath);
        await environment.InsertAsync("case-launch-failure", "report-launch-failure", extractPath, reportPath);

        var result = await environment.Service.OpenAsync(new ReportOpenRequest("report-launch-failure"));

        Assert.False(result.Success);
        Assert.Contains("默认浏览器", result.ErrorMessage);
        Assert.Null((await environment.Reports.GetAsync("report-launch-failure"))?.LastOpenedAt);
    }

    [Fact]
    public async Task OpenAsync_ReturnsChineseFailureWhenLastOpenedAtCannotBePersisted()
    {
        await using var environment = await TestEnvironment.CreateAsync(failLastOpenedUpdate: true);
        var extractPath = environment.CreateExtractDirectory("case-update-failure");
        var reportPath = await environment.CreateReportAsync(extractPath);
        await environment.InsertAsync("case-update-failure", "report-update-failure", extractPath, reportPath);

        var result = await environment.Service.OpenAsync(new ReportOpenRequest("report-update-failure"));

        Assert.True(result.Success);
        Assert.Contains("打开时间", result.ErrorMessage);
        Assert.Single(environment.Launcher.OpenedPaths);
    }

    [Fact]
    public async Task OpenAsync_RejectsExtractDirectoryReparsePoint()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var extractPath = environment.CreateExtractDirectory("case-extract-link");
        var reportPath = await environment.CreateReportAsync(extractPath);
        environment.RejectLinkedPath(extractPath);
        await environment.InsertAsync("case-extract-link", "report-extract-link", extractPath, reportPath);

        var result = await environment.Service.OpenAsync(new ReportOpenRequest("report-extract-link"));

        Assert.False(result.Success);
        Assert.Contains("链接", result.ErrorMessage);
        Assert.Empty(environment.Launcher.OpenedPaths);
    }

    [Fact]
    public async Task OpenAsync_RejectsIndexHtmlReparsePoint()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var extractPath = environment.CreateExtractDirectory("case-entry-link");
        var reportPath = await environment.CreateReportAsync(extractPath);
        environment.RejectLinkedPath(Path.Combine(reportPath, "index.html"));
        await environment.InsertAsync("case-entry-link", "report-entry-link", extractPath, reportPath);

        var result = await environment.Service.OpenAsync(new ReportOpenRequest("report-entry-link"));

        Assert.False(result.Success);
        Assert.Contains("链接", result.ErrorMessage);
        Assert.Empty(environment.Launcher.OpenedPaths);
    }

    [Fact]
    public async Task OpenAsync_RejectsReportDirectoryReparsePoint()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var extractPath = environment.CreateExtractDirectory("case-link");
        var reportPath = await environment.CreateReportAsync(extractPath);
        environment.RejectLinkedPath(reportPath);
        await environment.InsertAsync("case-link", "report-link", extractPath, reportPath);

        var result = await environment.Service.OpenAsync(new ReportOpenRequest("report-link"));

        Assert.False(result.Success);
        Assert.Contains("链接", result.ErrorMessage);
        Assert.Empty(environment.Launcher.OpenedPaths);
    }

    [Fact]
    public async Task OpenAsync_LaunchesIndexHtmlAndUpdatesLastOpenedAtAfterSuccess()
    {
        var openedAt = new DateTimeOffset(2026, 8, 23, 8, 30, 0, TimeSpan.Zero);
        await using var environment = await TestEnvironment.CreateAsync(openedAt: openedAt);
        var extractPath = environment.CreateExtractDirectory("case-success");
        var reportPath = await environment.CreateReportAsync(extractPath);
        await environment.InsertAsync("case-success", "report-success", extractPath, reportPath);

        var result = await environment.Service.OpenAsync(new ReportOpenRequest("report-success"));

        Assert.True(result.Success);
        Assert.Equal(Path.Combine(reportPath, "index.html"), result.ReportEntryPath);
        Assert.Equal(result.ReportEntryPath, Assert.Single(environment.Launcher.OpenedPaths));
        Assert.Equal(openedAt.UtcDateTime, (await environment.Reports.GetAsync("report-success"))?.LastOpenedAt);
    }

    private sealed class TestEnvironment : IAsyncDisposable
    {
        private TestEnvironment(
            string root,
            SqliteCaseRepository cases,
            SqliteReportRepository reports,
            RecordingReportProcessLauncher launcher,
            ReportOpenService service,
            RecordingReportPathSecurity pathSecurity)
        {
            Root = root;
            Cases = cases;
            Reports = reports;
            Launcher = launcher;
            Service = service;
            PathSecurity = pathSecurity;
        }

        public string Root { get; }
        public SqliteCaseRepository Cases { get; }
        public SqliteReportRepository Reports { get; }
        public RecordingReportProcessLauncher Launcher { get; }
        public ReportOpenService Service { get; }
        public RecordingReportPathSecurity PathSecurity { get; }

        public static async Task<TestEnvironment> CreateAsync(Exception? launchError = null, DateTimeOffset? openedAt = null, bool failLastOpenedUpdate = false)
        {
            var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
            var paths = new DataPaths(root);
            var factory = new SqliteConnectionFactory(paths);
            await new DatabaseInitializer(factory).InitializeAsync();
            var cases = new SqliteCaseRepository(factory);
            var reports = new SqliteReportRepository(factory);
            var launcher = new RecordingReportProcessLauncher(launchError);
            var logger = new WorkbenchLogger(root);
            var clock = new FixedTimeProvider(openedAt ?? DateTimeOffset.Now);
            var pathSecurity = new RecordingReportPathSecurity();
            IReportRepository repository = failLastOpenedUpdate ? new FailingUpdateReportRepository(reports) : reports;
            return new TestEnvironment(root, cases, reports, launcher, new ReportOpenService(cases, repository, launcher, logger, pathSecurity, clock), pathSecurity);
        }

        public string CreateExtractDirectory(string caseId)
        {
            var path = Path.Combine(Root, "Cases", caseId, "Extract");
            Directory.CreateDirectory(path);
            return path;
        }

        public async Task<string> CreateReportAsync(string extractPath)
        {
            var reportPath = Path.Combine(extractPath, "Report");
            Directory.CreateDirectory(reportPath);
            await File.WriteAllTextAsync(Path.Combine(reportPath, "index.html"), "<html></html>");
            return reportPath;
        }

        public void RejectLinkedPath(string path) => PathSecurity.LinkedPaths.Add(Path.GetFullPath(path));

        public async Task InsertAsync(string caseId, string reportId, string extractPath, string reportPath)
        {
            var now = DateTime.Now;
            await Cases.InsertAsync(new AnalysisCase
            {
                Id = caseId,
                DisplayName = caseId,
                OriginalName = $"{caseId}.tgz",
                DeviceId = "TEST",
                LogTime = now,
                Status = CaseStatus.Completed,
                SourcePath = Path.Combine(Root, $"{caseId}.tgz"),
                ExtractPath = extractPath,
                ReportPath = reportPath,
                CreateTime = now,
                UpdateTime = now
            });
            await Reports.InsertAsync(new Report
            {
                Id = reportId,
                CaseId = caseId,
                Path = reportPath,
                PluginId = "log-analyzer",
                CreateTime = now
            });
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingReportProcessLauncher(Exception? error) : IReportProcessLauncher
    {
        public List<string> OpenedPaths { get; } = new();

        public void Open(string reportEntryPath)
        {
            OpenedPaths.Add(reportEntryPath);
            if (error is not null) throw error;
        }
    }


    private sealed class RecordingReportPathSecurity : IReportPathSecurity
    {
        public HashSet<string> LinkedPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool IsReparsePoint(string path) => LinkedPaths.Contains(Path.GetFullPath(path));
    }

    private sealed class FailingUpdateReportRepository(IReportRepository inner) : IReportRepository
    {
        public Task InsertAsync(Report item, CancellationToken cancellationToken = default) => inner.InsertAsync(item, cancellationToken);
        public Task<Report?> GetAsync(string id, CancellationToken cancellationToken = default) => inner.GetAsync(id, cancellationToken);
        public Task<Report?> GetByCaseIdAsync(string caseId, CancellationToken cancellationToken = default) => inner.GetByCaseIdAsync(caseId, cancellationToken);
        public Task<IReadOnlyList<ReportSummary>> ListAsync(ReportQuery query, CancellationToken cancellationToken = default) => inner.ListAsync(query, cancellationToken);
        public Task UpdateLastOpenedAtAsync(string id, DateTime openedAt, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("模拟数据库写入失败");
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("测试时区", now.Offset, "测试时区", "测试时区");
    }
}

