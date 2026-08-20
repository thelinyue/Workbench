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
    public async Task ReportQuery_PersistsFilterAndCascadeWithCase()
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
            var plugins = new SqlitePluginInfoRepository(factory);
            var now = DateTime.Now;
            var extractDirectory = Path.Combine(root, "Extract");
            var reportDirectory = Path.Combine(extractDirectory, "report");
            Directory.CreateDirectory(reportDirectory);
            await File.WriteAllTextAsync(Path.Combine(reportDirectory, "report.html"), "<html></html>");
            await cases.InsertAsync(new AnalysisCase
            {
                Id = "case-report", DisplayName = "客户A网络异常", OriginalName = "diag_A.tgz", DeviceId = "EC661JJ",
                LogTime = now, Status = CaseStatus.Completed, SourcePath = Path.Combine(root, "source.tgz"), ExtractPath = extractDirectory,
                ReportPath = reportDirectory, CreateTime = now, UpdateTime = now
            });
            await plugins.UpsertAsync(new PluginInfo { Id = "network", Name = "Network Analyzer", Version = "1", Type = "exe", Path = "plugin", Entry = "run.exe" });
            await reports.InsertAsync(new Report { Id = "report-1", CaseId = "case-report", Path = reportDirectory, PluginId = "network", CreateTime = now });

            var filtered = await reports.ListAsync(new ReportQuery("客户A", "EC661", "network", now.Date, now.Date));
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
    public async Task DatabaseInitializer_UpgradesOldReportsTableAndBackfillsPlugin()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var factory = new SqliteConnectionFactory(new DataPaths(root));
            await using (var connection = await factory.OpenAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    CREATE TABLE analysis_cases (id TEXT PRIMARY KEY, display_name TEXT NOT NULL, original_name TEXT NOT NULL, device_id TEXT NOT NULL, log_time TEXT NOT NULL, status TEXT NOT NULL, source_path TEXT NOT NULL, extract_path TEXT NOT NULL, report_path TEXT NULL, error_message TEXT NULL, create_time TEXT NOT NULL, update_time TEXT NOT NULL);
                    CREATE TABLE analysis_tasks (id TEXT PRIMARY KEY, case_id TEXT NOT NULL, plugin_id TEXT NOT NULL, status TEXT NOT NULL, start_time TEXT NULL, end_time TEXT NULL, report_path TEXT NULL, error_message TEXT NULL);
                    CREATE TABLE reports (id TEXT PRIMARY KEY, case_id TEXT NOT NULL, path TEXT NOT NULL, create_time TEXT NOT NULL);
                    INSERT INTO analysis_cases VALUES ('case-1','旧案例','diag.tgz','A','2026-08-11T00:00:00','Completed','s','e','r',NULL,'2026-08-11T00:00:00','2026-08-11T00:00:00');
                    INSERT INTO analysis_tasks VALUES ('task-1','case-1','legacy-plugin','Completed','2026-08-11T00:00:00','2026-08-11T00:01:00','r',NULL);
                    INSERT INTO reports VALUES ('report-1','case-1','r','2026-08-11T00:01:00');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            await new DatabaseInitializer(factory).InitializeAsync();
            var report = await new SqliteReportRepository(factory).GetAsync("report-1");
            Assert.Equal("legacy-plugin", report?.PluginId);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
