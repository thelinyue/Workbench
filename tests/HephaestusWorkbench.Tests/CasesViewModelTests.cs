using HephaestusWorkbench.App.ViewModels;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Data;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

public sealed class CasesViewModelTests
{
    [Fact]
    public async Task OpenExtractDirectory_UsesSelectedCaseExtractPath()
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
            var logger = new WorkbenchLogger(root);
            var analysis = new CaseAnalysisService(
                paths,
                cases,
                tasks,
                reports,
                new PluginCatalog(paths, logger),
                new LegacyLogAnalyzerRunner(logger),
                new StandardExePluginRunner(logger),
                new TaskCenter(tasks),
                logger);
            var extractPath = Path.Combine(root, "Extract");
            await cases.InsertAsync(new AnalysisCase
            {
                Id = "case-1",
                DisplayName = "测试案例",
                OriginalName = "diag_DEVICE01_2608111530.tgz",
                DeviceId = "DEVICE01",
                LogTime = DateTime.Now,
                Status = CaseStatus.Completed,
                SourcePath = Path.Combine(root, "diag_DEVICE01_2608111530.tgz"),
                ExtractPath = extractPath,
                ReportPath = Path.Combine(root, "Report"),
                CreateTime = DateTime.Now,
                UpdateTime = DateTime.Now
            });
            string? openedExtractPath = null;
            var viewModel = new CasesViewModel(analysis, (_, _) => { }, path => openedExtractPath = path);

            await viewModel.SelectCaseAsync("case-1");
            viewModel.OpenExtractDirectoryCommand.Execute(null);

            Assert.Equal(extractPath, openedExtractPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
