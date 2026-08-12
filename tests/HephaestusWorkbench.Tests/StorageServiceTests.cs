using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Data;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

public sealed class StorageServiceTests
{
    [Fact]
    public async Task GetSummaryAsync_DoesNotDoubleCountReportInsideExtract()
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
            var reportPath = paths.GetReportDirectory(extractPath);
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

            var summary = await new StorageService(paths, cases).GetSummaryAsync();
            Assert.Equal(new FileInfo(sourcePath).Length, summary.LogBytes);
            Assert.Equal(new FileInfo(Path.Combine(extractPath, "system.log")).Length, summary.ExtractBytes);
            Assert.Equal(new FileInfo(Path.Combine(reportPath, "report.html")).Length, summary.ReportBytes);
            Assert.Equal(summary.LogBytes + summary.ExtractBytes + summary.ReportBytes, summary.ReleasableBytes);
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

            var service = new StorageService(paths, cases);
            var summary = await service.GetSummaryAsync();

            Assert.True(File.Exists(sourcePath));
            Assert.True(Directory.Exists(sourceDirectory));
            Assert.True(summary.LogBytes > 0);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
