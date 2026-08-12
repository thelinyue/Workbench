using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Data;
using HephaestusWorkbench.Services;
using AnalysisTaskStatus = HephaestusWorkbench.Core.Models.TaskStatus;

namespace HephaestusWorkbench.Tests;

public sealed class CaseAnalysisLifecycleTests
{
    [Fact]
    public async Task DeleteLifecycle_RemovesEveryCaseSharingTheSourceAndTheirArtifacts()
    {
        var environment = await CreateEnvironmentAsync();
        try
        {
            var source = Path.Combine(environment.Root, "Inbox", "diag_DEVICE01_2608111530.tgz");
            var extract = Path.Combine(environment.Root, "Inbox", "diag_DEVICE01_2608111530");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            Directory.CreateDirectory(extract);
            await File.WriteAllTextAsync(source, "source");
            await File.WriteAllTextAsync(Path.Combine(extract, "extract.log"), "extract");
            foreach (var id in new[] { "case-1", "case-2" })
            {
                var item = Case(id, source, extract, CaseStatus.Completed);
                await environment.Cases.InsertAsync(item);
                await environment.Tasks.InsertAsync(Task(id, AnalysisTaskStatus.Completed));
                var reportPath = environment.Paths.GetReportDirectory(extract);
                Directory.CreateDirectory(reportPath);
                await File.WriteAllTextAsync(Path.Combine(reportPath, "report.html"), "report");
            }

            await environment.Analysis.DeleteLifecycleAsync(source);

            Assert.Empty(await environment.Cases.ListAsync());
            Assert.Empty(await environment.Tasks.ListAsync());
            Assert.False(File.Exists(source));
            Assert.False(Directory.Exists(extract));
            Assert.False(Directory.Exists(environment.Paths.GetCaseDirectory("case-1")));
            Assert.False(Directory.Exists(environment.Paths.GetCaseDirectory("case-2")));
        }
        finally
        {
            if (Directory.Exists(environment.Root)) Directory.Delete(environment.Root, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteLifecycle_RejectsGroupWithActiveTaskBeforeDeletingAnything()
    {
        var environment = await CreateEnvironmentAsync();
        try
        {
            var source = Path.Combine(environment.Root, "Inbox", "diag_DEVICE01_2608111530.tgz");
            var extract = Path.Combine(environment.Root, "Inbox", "diag_DEVICE01_2608111530");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            Directory.CreateDirectory(extract);
            await File.WriteAllTextAsync(source, "source");
            await environment.Cases.InsertAsync(Case("case-1", source, extract, CaseStatus.Running));
            await environment.Tasks.InsertAsync(Task("case-1", AnalysisTaskStatus.Running));

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => environment.Analysis.DeleteLifecycleAsync(source));

            Assert.Contains("等待或运行", error.Message);
            Assert.True(File.Exists(source));
            Assert.True(Directory.Exists(extract));
            Assert.NotNull(await environment.Cases.GetAsync("case-1"));
        }
        finally
        {
            if (Directory.Exists(environment.Root)) Directory.Delete(environment.Root, recursive: true);
        }
    }

    [Fact]
    public async Task CleanupExpiredAsync_DeletesExpiredCompletedLifecycle()
    {
        var environment = await CreateEnvironmentAsync();
        try
        {
            var source = Path.Combine(environment.Root, "Inbox", "diag_DEVICE01_2608111530.tgz");
            var extract = Path.Combine(environment.Root, "Inbox", "diag_DEVICE01_2608111530");
            var reportPath = environment.Paths.GetReportDirectory(extract);
            var old = DateTime.Now.AddDays(-8);
            Directory.CreateDirectory(reportPath);
            await File.WriteAllTextAsync(source, "source");
            await File.WriteAllTextAsync(Path.Combine(extract, "extract.log"), "extract");
            await File.WriteAllTextAsync(Path.Combine(reportPath, "report.html"), "report");
            await environment.Cases.InsertAsync(new AnalysisCase
            {
                Id = "case-old", DisplayName = "case-old", OriginalName = Path.GetFileName(source), DeviceId = "DEVICE01",
                LogTime = old, Status = CaseStatus.Completed, SourcePath = source, ExtractPath = extract,
                ReportPath = reportPath, CreateTime = old, UpdateTime = old
            });
            await environment.Tasks.InsertAsync(Task("case-old", AnalysisTaskStatus.Completed));
            await environment.Reports.InsertAsync(new Report { Id = "report-old", CaseId = "case-old", Path = Path.Combine(environment.Root, "Cases", "case-old", "Report"), CreateTime = old });

            var result = await environment.Analysis.CleanupExpiredAsync(7);

            Assert.Equal(1, result.Deleted);
            Assert.False(File.Exists(source));
            Assert.False(Directory.Exists(extract));
            Assert.Null(await environment.Cases.GetAsync("case-old"));
            Assert.Empty(await environment.Reports.ListAsync(new ReportQuery()));
        }
        finally
        {
            if (Directory.Exists(environment.Root)) Directory.Delete(environment.Root, recursive: true);
        }
    }

    private static async Task<TestEnvironment> CreateEnvironmentAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        var paths = new DataPaths(root);
        var factory = new SqliteConnectionFactory(paths);
        await new DatabaseInitializer(factory).InitializeAsync();
        var cases = new SqliteCaseRepository(factory);
        var tasks = new SqliteTaskRepository(factory);
        var reports = new SqliteReportRepository(factory);
        var logger = new WorkbenchLogger(root);
        var analysis = new CaseAnalysisService(paths, cases, tasks, reports, new PluginCatalog(paths, logger), new LegacyLogAnalyzerRunner(logger), new StandardExePluginRunner(logger), new TaskCenter(tasks), logger);
        return new TestEnvironment(root, paths, cases, tasks, reports, analysis);
    }

    private static AnalysisCase Case(string id, string source, string extract, CaseStatus status) => new()
    {
        Id = id,
        DisplayName = id,
        OriginalName = Path.GetFileName(source),
        DeviceId = "DEVICE01",
        LogTime = DateTime.Now,
        Status = status,
        SourcePath = source,
        ExtractPath = extract,
        ReportPath = status == CaseStatus.Completed ? "report" : null,
        CreateTime = DateTime.Now,
        UpdateTime = DateTime.Now
    };

    private static AnalysisTask Task(string caseId, AnalysisTaskStatus status) => new()
    {
        Id = $"task-{caseId}",
        CaseId = caseId,
        PluginId = "test-plugin",
        Status = status,
        StartTime = DateTime.Now,
        EndTime = status == AnalysisTaskStatus.Completed ? DateTime.Now : null
    };

    private sealed record TestEnvironment(string Root, DataPaths Paths, SqliteCaseRepository Cases, SqliteTaskRepository Tasks, SqliteReportRepository Reports, CaseAnalysisService Analysis);
}
