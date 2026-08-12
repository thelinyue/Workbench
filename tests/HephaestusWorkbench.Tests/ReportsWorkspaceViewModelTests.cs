using HephaestusWorkbench.App.ViewModels;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Data;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

public sealed class ReportsWorkspaceViewModelTests
{
    [Fact]
    public async Task OpenReport_DeduplicatesAndClosesOldestAtConfiguredLimit()
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
            var sessions = new SqliteReportSessionRepository(factory);
            var settingsStore = new SqliteSettingsStore(factory);
            var logger = new WorkbenchLogger(root);
            var catalog = new PluginCatalog(paths, logger);
            var taskCenter = new TaskCenter(tasks);
            var analysis = new CaseAnalysisService(paths, cases, tasks, reports, catalog, new LegacyLogAnalyzerRunner(logger), new StandardExePluginRunner(logger), taskCenter, logger);
            var settings = new SettingsService(settingsStore, paths.InboxDirectory);
            await settings.SetReportRestoreEnabledAsync(false);
            await settings.SetReportMaxTabsAsync(10);
            var openedExtractPaths = new List<string>();
            using var workspace = new ReportsWorkspaceViewModel(new ReportService(reports, sessions, analysis), settings, _ => { }, openedExtractPaths.Add, logger, _ => true);

            var first = Summary(0);
            await workspace.OpenReportAsync(first);
            await workspace.OpenReportAsync(first);
            Assert.Single(workspace.OpenTabs);
            workspace.OpenSelectedExtractDirectoryCommand.Execute(null);
            Assert.Equal(new[] { first.ExtractPath }, openedExtractPaths);

            for (var index = 1; index <= 10; index++) await workspace.OpenReportAsync(Summary(index));
            Assert.Equal(10, workspace.OpenTabs.Count);
            Assert.DoesNotContain(workspace.OpenTabs, x => x.Report.Id == first.Id);
            Assert.Equal("report-10", workspace.SelectedTab?.Report.Id);

            var selected = workspace.SelectedTab!;
            workspace.CloseTab(selected);
            Assert.Equal("report-9", workspace.SelectedTab?.Report.Id);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InitializeAsync_LoadsReportLibraryWhenRestoreIsDisabled()
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
            var sessions = new SqliteReportSessionRepository(factory);
            var settingsStore = new SqliteSettingsStore(factory);
            var logger = new WorkbenchLogger(root);
            var analysis = new CaseAnalysisService(paths, cases, tasks, reports, new PluginCatalog(paths, logger), new LegacyLogAnalyzerRunner(logger), new StandardExePluginRunner(logger), new TaskCenter(tasks), logger);
            var settings = new SettingsService(settingsStore, paths.InboxDirectory);
            await settings.SetReportRestoreEnabledAsync(false);
            var reportPath = paths.GetCaseReportDirectory("case-report");
            Directory.CreateDirectory(reportPath);
            await File.WriteAllTextAsync(Path.Combine(reportPath, "report.html"), "<html>ok</html>");
            await cases.InsertAsync(new AnalysisCase
            {
                Id = "case-report",
                DisplayName = "客户A网络异常",
                OriginalName = "diag_A.tgz",
                DeviceId = "DEVICE01",
                LogTime = DateTime.Now,
                Status = CaseStatus.Completed,
                SourcePath = "source",
                ExtractPath = "extract",
                ReportPath = reportPath,
                CreateTime = DateTime.Now,
                UpdateTime = DateTime.Now
            });
            await reports.InsertAsync(new Report
            {
                Id = "report-1",
                CaseId = "case-report",
                Path = reportPath,
                PluginId = "plugin",
                CreateTime = DateTime.Now
            });

            using var workspace = new ReportsWorkspaceViewModel(new ReportService(reports, sessions, analysis), settings, _ => { }, _ => { }, logger, _ => true);
            await workspace.InitializeAsync();

            var report = Assert.Single(workspace.Library.Items);
            Assert.Equal("report-1", report.Id);
            Assert.False(workspace.Library.ShowEmptyState);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static ReportSummary Summary(int index) => new()
    {
        Id = $"report-{index}", CaseId = $"case-{index}", CaseName = $"案例 {index}", DeviceId = $"device-{index}",
        Path = Path.GetTempPath(), ExtractPath = Path.Combine(Path.GetTempPath(), $"extract-{index}"), PluginId = "plugin", PluginName = "插件", CreateTime = DateTime.Now.AddMinutes(index), IsAvailable = true
    };
}
