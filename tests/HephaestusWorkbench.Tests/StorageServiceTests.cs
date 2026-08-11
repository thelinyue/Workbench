using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Data;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

public sealed class StorageServiceTests
{
    [Fact]
    public async Task CleanCaseDataAsync_RemovesOriginalArtifactsAndKeepsReport()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new DataPaths(root);
            var factory = new SqliteConnectionFactory(paths);
            await new DatabaseInitializer(factory).InitializeAsync();
            var cases = new SqliteCaseRepository(factory);
            var now = DateTime.Now;
            var sourceDirectory = Path.Combine(root, "OriginalLogs");
            var sourcePath = Path.Combine(sourceDirectory, "diag_A_202608111200.tgz");
            var extractPath = Path.Combine(sourceDirectory, "diag_A_202608111200");
            var reportPath = paths.GetCaseReportDirectory("case-1");
            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(extractPath);
            Directory.CreateDirectory(reportPath);
            await File.WriteAllTextAsync(sourcePath, "archive");
            await File.WriteAllTextAsync(Path.Combine(extractPath, "system.log"), "extracted");
            await File.WriteAllTextAsync(Path.Combine(reportPath, "report.html"), "<html></html>");
            await cases.InsertAsync(new AnalysisCase
            {
                Id = "case-1",
                DisplayName = "测试案例",
                OriginalName = Path.GetFileName(sourcePath),
                DeviceId = "A",
                LogTime = now,
                Status = CaseStatus.Completed,
                SourcePath = sourcePath,
                ExtractPath = extractPath,
                ReportPath = reportPath,
                CreateTime = now,
                UpdateTime = now
            });

            await new StorageService(paths, cases).CleanCaseDataAsync("case-1");

            Assert.False(File.Exists(sourcePath));
            Assert.False(Directory.Exists(extractPath));
            Assert.True(File.Exists(Path.Combine(reportPath, "report.html")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CleanCaseDataAsync_RejectsExtractPathThatIsNotSourceSibling()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new DataPaths(root);
            var factory = new SqliteConnectionFactory(paths);
            await new DatabaseInitializer(factory).InitializeAsync();
            var cases = new SqliteCaseRepository(factory);
            var now = DateTime.Now;
            var sourceDirectory = Path.Combine(root, "OriginalLogs");
            var sourcePath = Path.Combine(sourceDirectory, "diag_A_202608111200.tgz");
            var unsafeExtractPath = sourceDirectory;
            Directory.CreateDirectory(sourceDirectory);
            await File.WriteAllTextAsync(sourcePath, "archive");
            await cases.InsertAsync(new AnalysisCase
            {
                Id = "case-1",
                DisplayName = "不安全案例",
                OriginalName = Path.GetFileName(sourcePath),
                DeviceId = "A",
                LogTime = now,
                Status = CaseStatus.Completed,
                SourcePath = sourcePath,
                ExtractPath = unsafeExtractPath,
                CreateTime = now,
                UpdateTime = now
            });

            await Assert.ThrowsAsync<InvalidOperationException>(() => new StorageService(paths, cases).CleanCaseDataAsync("case-1"));

            Assert.True(File.Exists(sourcePath));
            Assert.True(Directory.Exists(sourceDirectory));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
