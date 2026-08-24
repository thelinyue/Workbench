using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Data;
using AnalysisTaskStatus = HephaestusWorkbench.Core.Models.TaskStatus;

namespace HephaestusWorkbench.Tests;

public sealed class SqliteRepositoryTests
{
    [Fact]
    public async Task Repositories_PersistCaseTaskAndReport()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new DataPaths(root);
            var factory = new SqliteConnectionFactory(paths);
            await new DatabaseInitializer(factory).InitializeAsync();
            var cases = new SqliteCaseRepository(factory);
            var tasks = new SqliteTaskRepository(factory);
            var reports = new SqliteReportRepository(factory);
            var now = DateTime.Now;
            var item = new AnalysisCase
            {
                Id = "case-1",
                DisplayName = "测试案例",
                OriginalName = "diag_A_202608110952.tgz",
                DeviceId = "A",
                LogTime = now,
                Status = CaseStatus.Ready,
                SourcePath = Path.Combine(root, "Cases", "case-1", "Source"),
                ExtractPath = Path.Combine(root, "Cases", "case-1", "Extract"),
                CreateTime = now,
                UpdateTime = now
            };

            await cases.InsertAsync(item);
            await tasks.InsertAsync(new AnalysisTask { Id = "task-1", CaseId = item.Id, PluginId = "plugin", Status = AnalysisTaskStatus.Waiting });
            await reports.InsertAsync(new Report { Id = "report-1", CaseId = item.Id, Path = "report", PluginId = "plugin", CreateTime = now });

            var savedCase = await cases.GetAsync(item.Id);
            var savedTask = await tasks.GetAsync("task-1");
            var savedReport = await reports.GetByCaseIdAsync(item.Id);
            Assert.Equal("测试案例", savedCase?.DisplayName);
            Assert.Equal(AnalysisTaskStatus.Waiting, savedTask?.Status);
            Assert.Equal("report", savedReport?.Path);
            Assert.Equal("plugin", savedReport?.PluginId);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReportQuery_UsesStoredPluginNameAndCascadesWithCase()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new DataPaths(root);
            var factory = new SqliteConnectionFactory(paths);
            await new DatabaseInitializer(factory).InitializeAsync();
            var cases = new SqliteCaseRepository(factory);
            var reports = new SqliteReportRepository(factory);
            var now = DateTime.Now;
            var extractDirectory = Path.Combine(root, "Extract");
            var reportDirectory = Path.Combine(extractDirectory, "Report");
            Directory.CreateDirectory(reportDirectory);
            await File.WriteAllTextAsync(Path.Combine(reportDirectory, "index.html"), "<html></html>");
            await cases.InsertAsync(new AnalysisCase
            {
                Id = "case-report", DisplayName = "客户A网络异常", OriginalName = "diag_A.tgz", DeviceId = "EC661JJ",
                LogTime = now, Status = CaseStatus.Completed, SourcePath = Path.Combine(root, "source.tgz"), ExtractPath = extractDirectory,
                ReportPath = reportDirectory, CreateTime = now, UpdateTime = now
            });
            await reports.InsertAsync(new Report
            {
                Id = "report-1",
                CaseId = "case-report",
                Path = reportDirectory,
                PluginId = "network",
                PluginName = "Network Analyzer",
                CreateTime = now
            });

            var filtered = await reports.ListAsync(new ReportQuery("Network Analyzer", "EC661", "network", now.Date, now.Date));
            Assert.Single(filtered);
            Assert.True(filtered[0].IsAvailable);
            Assert.Equal(extractDirectory, filtered[0].ExtractPath);
            Assert.Equal("Network Analyzer", filtered[0].PluginName);

            await cases.DeleteAsync("case-report");
            Assert.Empty(await reports.ListAsync(new ReportQuery()));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReportQuery_FallsBackToPluginIdWhenStoredNameIsMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new DataPaths(root);
            var factory = new SqliteConnectionFactory(paths);
            await new DatabaseInitializer(factory).InitializeAsync();
            var cases = new SqliteCaseRepository(factory);
            var reports = new SqliteReportRepository(factory);
            var now = DateTime.Now;
            await cases.InsertAsync(new AnalysisCase
            {
                Id = "case-plugin-fallback",
                DisplayName = "插件名称回退",
                OriginalName = "diag_A.tgz",
                DeviceId = "A",
                LogTime = now,
                Status = CaseStatus.Completed,
                SourcePath = Path.Combine(root, "source.tgz"),
                ExtractPath = Path.Combine(root, "Extract"),
                CreateTime = now,
                UpdateTime = now
            });
            await reports.InsertAsync(new Report
            {
                Id = "report-plugin-fallback",
                CaseId = "case-plugin-fallback",
                Path = Path.Combine(root, "Extract", "report"),
                PluginId = "storage-analyzer",
                CreateTime = now
            });

            var filtered = await reports.ListAsync(new ReportQuery(Keyword: "storage-analyzer"));

            var summary = Assert.Single(filtered);
            Assert.Equal("storage-analyzer", summary.PluginName);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
