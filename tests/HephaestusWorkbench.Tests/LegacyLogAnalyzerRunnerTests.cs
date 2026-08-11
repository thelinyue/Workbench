using HephaestusWorkbench.PluginSDK;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

public sealed class LegacyLogAnalyzerRunnerTests
{
    [Fact]
    public async Task RunAsync_WritesReportToOutputWithoutMovingInputDirectory()
    {
        var pluginPath = Path.Combine(AppContext.BaseDirectory, "PluginSeed", "log_analyzer.exe");
        Assert.True(File.Exists(pluginPath), $"测试插件不存在：{pluginPath}");

        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        var inputDirectory = Path.Combine(root, "OriginalLogs");
        var outputDirectory = Path.Combine(root, "CaseReport");
        Directory.CreateDirectory(inputDirectory);
        await File.WriteAllTextAsync(Path.Combine(inputDirectory, "sample.log"), "runner smoke test");
        try
        {
            var manifest = new PluginManifest
            {
                Id = "log-analyzer",
                Name = "日志分析插件",
                Version = "1.50",
                Type = PluginType.Exe,
                Entry = "log_analyzer.exe",
                Runner = "legacy-log-analyzer",
                DirectoryPath = Path.GetDirectoryName(pluginPath)!
            };
            var context = new PluginExecutionContext(
                "case-1",
                inputDirectory,
                outputDirectory,
                Path.Combine(root, "ShouldNotBeCreated"),
                root);
            var logger = new WorkbenchLogger(root);

            var result = await new LegacyLogAnalyzerRunner(logger).RunAsync(manifest, context);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(outputDirectory, result.ReportPath);
            Assert.True(File.Exists(Path.Combine(outputDirectory, "report.html")));
            Assert.True(File.Exists(Path.Combine(inputDirectory, "sample.log")));
            Assert.False(Directory.Exists(context.ExtractPath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
