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
            using var workspace = new ReportsWorkspaceViewModel(new ReportService(reports, sessions, analysis), settings, openedExtractPaths.Add, logger, _ => true);

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

    private static ReportSummary Summary(int index) => new()
    {
        Id = $"report-{index}", CaseId = $"case-{index}", CaseName = $"案例 {index}", DeviceId = $"device-{index}",
        Path = Path.GetTempPath(), ExtractPath = Path.Combine(Path.GetTempPath(), $"extract-{index}"), PluginId = "plugin", PluginName = "插件", CreateTime = DateTime.Now.AddMinutes(index), IsAvailable = true
    };
}
