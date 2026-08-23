using HephaestusWorkbench.PluginSDK;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

public sealed class ProcessPluginRunnerReportTests
{
    [Fact]
    public async Task StandardRunner_ReturnsManifestReports()
    {
        var root = CreateRoot();
        try
        {
            var output = Path.Combine(root, "report");
            var runner = new StandardExePluginRunner(new WorkbenchLogger(root), async (_, _, _, _) =>
            {
                Directory.CreateDirectory(output);
                await File.WriteAllTextAsync(Path.Combine(output, "storage.html"), "<html></html>");
                await File.WriteAllTextAsync(Path.Combine(output, "log.html"), "<html></html>");
                await File.WriteAllTextAsync(Path.Combine(output, "reports.json"), ValidManifest);
                return (0, string.Empty);
            });

            var result = await runner.RunAsync(Manifest(), Context(root, output));

            Assert.Equal(output, result.ReportPath);
            Assert.Equal(2, result.Reports?.Count);
            Assert.True(result.Reports?.Single(x => x.Id == "storage").IsDefault);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task LegacyRunner_InvalidManifestFailsWithoutLegacyFallback()
    {
        var root = CreateRoot();
        try
        {
            var output = Path.Combine(root, "report");
            var runner = new LegacyLogAnalyzerRunner(new WorkbenchLogger(root), async (_, _, _, _) =>
            {
                Directory.CreateDirectory(output);
                await File.WriteAllTextAsync(Path.Combine(output, "report.html"), "<html>legacy</html>");
                await File.WriteAllTextAsync(Path.Combine(output, "reports.json"), "{ \"schemaVersion\": 2, \"reports\": [] }");
                return (0, string.Empty);
            });

            var result = await runner.RunAsync(Manifest("legacy-log-analyzer"), Context(root, output));

            Assert.Null(result.ReportPath);
            Assert.Null(result.Reports);
            Assert.Contains("报告清单", result.ErrorMessage);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private const string ValidManifest = """
        { "schemaVersion": 1, "reports": [
          { "id": "storage", "title": "存储健康诊断报告", "kind": "storage-health", "file": "storage.html", "isDefault": true },
          { "id": "log", "title": "综合日志分析报告", "kind": "log-analysis", "file": "log.html", "isDefault": false }
        ] }
        """;

    private static PluginManifest Manifest(string? runner = null) => new()
    {
        Id = "plugin", Name = "插件", Version = "1.1.0", Type = PluginType.Exe, Entry = "plugin.exe", Runner = runner, DirectoryPath = Path.GetTempPath()
    };

    private static PluginExecutionContext Context(string root, string output)
        => new("case", Path.Combine(root, "source"), output, Path.Combine(root, "extract"), root);

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
