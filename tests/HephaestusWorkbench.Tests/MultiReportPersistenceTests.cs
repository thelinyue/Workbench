using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Data;
using HephaestusWorkbench.Services;
using AnalysisTaskStatus = HephaestusWorkbench.Core.Models.TaskStatus;

namespace HephaestusWorkbench.Tests;

public sealed class MultiReportPersistenceTests
{
    [Fact]
    public async Task CompleteAsync_PersistsAllReportsFromOneAnalysisTransaction()
    {
        var root = CreateRoot();
        try
        {
            var paths = new DataPaths(root);
            var factory = new SqliteConnectionFactory(paths);
            await new DatabaseInitializer(factory).InitializeAsync();
            var lifecycle = new SqliteAnalysisLifecycleRepository(factory);
            var reports = new SqliteReportRepository(factory);
            var now = DateTime.Now;
            var analysisCase = NewCase("case-multi", now);
            var task = NewTask(analysisCase.Id);
            await lifecycle.CreateAsync(analysisCase, task);
            analysisCase.Status = CaseStatus.Completed;
            analysisCase.ReportPath = Path.Combine(root, "report");
            task.Status = AnalysisTaskStatus.Completed;
            task.ReportPath = analysisCase.ReportPath;
            task.EndTime = now;

            await lifecycle.CompleteAsync(analysisCase, task, new[]
            {
                NewReport("report-storage", analysisCase.Id, analysisCase.ReportPath, "storage-health", "Linux 存储健康诊断报告", "storage-health", "storage-health-report.html", true, now),
                NewReport("report-log", analysisCase.Id, analysisCase.ReportPath, "log-analysis", "综合日志分析报告", "log-analysis", "log-analysis-report.html", false, now)
            });

            var saved = await reports.ListAsync(new ReportQuery());
            Assert.Equal(2, saved.Count);
            Assert.Contains(saved, x => x.ReportKey == "storage-health" && x.Title == "Linux 存储健康诊断报告" && x.IsDefault);
            Assert.Contains(saved, x => x.ReportKey == "log-analysis" && x.Kind == "log-analysis" && x.EntryFile == "log-analysis-report.html");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task GetLatestForCaseAsync_PrefersDefaultReportFromLatestBatch()
    {
        var root = CreateRoot();
        try
        {
            var paths = new DataPaths(root);
            var factory = new SqliteConnectionFactory(paths);
            await new DatabaseInitializer(factory).InitializeAsync();
            var cases = new SqliteCaseRepository(factory);
            var repository = new SqliteReportRepository(factory);
            var now = DateTime.Now;
            var reportDirectory = Path.Combine(root, "extract", "report");
            Directory.CreateDirectory(reportDirectory);
            foreach (var file in new[] { "old.html", "storage.html", "log.html" })
                await File.WriteAllTextAsync(Path.Combine(reportDirectory, file), "<html></html>");
            await cases.InsertAsync(NewCase("case-latest", now, Path.Combine(root, "extract")));
            await repository.InsertAsync(NewReport("old", "case-latest", reportDirectory, "old", "旧报告", "log-analysis", "old.html", true, now.AddMinutes(-1)));
            await repository.InsertAsync(NewReport("new-log", "case-latest", reportDirectory, "log", "综合日志分析报告", "log-analysis", "log.html", false, now));
            await repository.InsertAsync(NewReport("new-storage", "case-latest", reportDirectory, "storage", "存储健康诊断报告", "storage-health", "storage.html", true, now));
            var analysis = TestAnalysisService(paths, factory, cases, repository);
            var service = new ReportService(repository, analysis);

            var latest = await service.GetLatestForCaseAsync("case-latest");
            var listed = await repository.ListAsync(new ReportQuery());

            Assert.Equal("new-storage", latest?.Id);
            Assert.Equal(Path.Combine(reportDirectory, "storage.html"), latest?.ReportFile);
            Assert.Equal(3, listed.Count);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static CaseAnalysisService TestAnalysisService(DataPaths paths, SqliteConnectionFactory factory, SqliteCaseRepository cases, SqliteReportRepository reports)
    {
        var logger = new WorkbenchLogger(paths.Root);
        var tasks = new SqliteTaskRepository(factory);
        return new CaseAnalysisService(paths, cases, tasks, reports, new PluginCatalog(paths, logger), new LegacyLogAnalyzerRunner(logger), new StandardExePluginRunner(logger), new TaskCenter(tasks), logger, new SqliteAnalysisLifecycleRepository(factory));
    }

    private static AnalysisCase NewCase(string id, DateTime now, string? extractPath = null) => new()
    {
        Id = id,
        DisplayName = "案例",
        OriginalName = "diag.tgz",
        DeviceId = "device",
        LogTime = now,
        Status = CaseStatus.Ready,
        SourcePath = Path.Combine(Path.GetTempPath(), $"{id}.tgz"),
        ExtractPath = extractPath ?? Path.Combine(Path.GetTempPath(), id),
        CreateTime = now,
        UpdateTime = now
    };

    private static AnalysisTask NewTask(string caseId) => new()
    {
        Id = $"task-{caseId}",
        CaseId = caseId,
        PluginId = "log-analyzer",
        Status = AnalysisTaskStatus.Waiting
    };

    private static Report NewReport(string id, string caseId, string path, string key, string title, string kind, string entryFile, bool isDefault, DateTime createTime) => new()
    {
        Id = id,
        CaseId = caseId,
        Path = path,
        ReportKey = key,
        Title = title,
        Kind = kind,
        EntryFile = entryFile,
        IsDefault = isDefault,
        PluginId = "log-analyzer",
        PluginName = "日志分析",
        PluginVersion = "1.1.0",
        CreateTime = createTime
    };

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
