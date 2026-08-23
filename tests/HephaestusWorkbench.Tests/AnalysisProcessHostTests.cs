using HephaestusWorkbench.PluginSDK;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

public sealed class AnalysisProcessHostTests
{
    [Fact]
    public async Task RunAsync_UsesJsonProtocolAndReturnsOnlyFixedReportDirectory()
    {
        using var environment = await TestEnvironment.CreateAsync("success");

        var result = await environment.Host.RunAsync(environment.Manifest, environment.Request);

        Assert.True(result.Succeeded);
        Assert.False(result.Cancelled);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Path.Combine(environment.ExtractDirectory, "Report"), result.ReportDirectory);
        Assert.Null(result.ErrorMessage);
        Assert.True(File.Exists(Path.Combine(result.ReportDirectory!, "index.html")));
    }

    [Fact]
    public async Task RunAsync_RejectsResponseForAnotherRequest()
    {
        using var environment = await TestEnvironment.CreateAsync("mismatch");

        var result = await environment.Host.RunAsync(environment.Manifest, environment.Request);

        Assert.False(result.Succeeded);
        Assert.Contains("请求标识", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_RejectsSuccessWithoutFixedReportEntry()
    {
        using var environment = await TestEnvironment.CreateAsync("missing-report");

        var result = await environment.Host.RunAsync(environment.Manifest, environment.Request);

        Assert.False(result.Succeeded);
        Assert.Contains("Report", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("index.html", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ReturnsExtensionFailureAsChineseError()
    {
        using var environment = await TestEnvironment.CreateAsync("failure");

        var result = await environment.Host.RunAsync(environment.Manifest, environment.Request);

        Assert.False(result.Succeeded);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("测试分析失败", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_RejectsOutputThatExceedsProtocolLimit()
    {
        using var environment = await TestEnvironment.CreateAsync("large-stdout");

        var result = await environment.Host.RunAsync(environment.Manifest, environment.Request);

        Assert.False(result.Succeeded);
        Assert.Contains("输出超过", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_CancellationStopsTheExtensionProcess()
    {
        using var environment = await TestEnvironment.CreateAsync("sleep");
        using var cancellation = new CancellationTokenSource();

        var runTask = environment.Host.RunAsync(environment.Manifest, environment.Request, cancellation.Token);
        var marker = Path.Combine(environment.ExtractDirectory, "fixture.started");
        await WaitForFileAsync(marker);
        var processId = int.Parse(await File.ReadAllTextAsync(marker));

        cancellation.Cancel();
        var result = await runTask;

        Assert.False(result.Succeeded);
        Assert.True(result.Cancelled);
        Assert.Contains("已取消", result.ErrorMessage, StringComparison.Ordinal);
        await WaitForProcessExitAsync(processId);
    }

    private static async Task WaitForFileAsync(string path)
    {
        for (var attempt = 0; attempt < 100 && !File.Exists(path); attempt++)
            await Task.Delay(50);
        Assert.True(File.Exists(path), $"测试进程未创建启动标记：{path}");
    }

    private static async Task WaitForProcessExitAsync(int processId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(processId);
                if (process.HasExited) return;
            }
            catch (ArgumentException)
            {
                return;
            }
            await Task.Delay(50);
        }
        Assert.Fail($"分析扩展进程 {processId} 在取消后仍未退出。");
    }

    private sealed class TestEnvironment : IDisposable
    {
        private TestEnvironment(
            string root,
            string extractDirectory,
            AnalysisProcessHost host,
            ExtensionManifest manifest,
            AnalysisProcessRequest request)
        {
            Root = root;
            ExtractDirectory = extractDirectory;
            Host = host;
            Manifest = manifest;
            Request = request;
        }

        public string Root { get; }
        public string ExtractDirectory { get; }
        public AnalysisProcessHost Host { get; }
        public ExtensionManifest Manifest { get; }
        public AnalysisProcessRequest Request { get; }

        public static async Task<TestEnvironment> CreateAsync(string mode)
        {
            var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
            var extractDirectory = Path.Combine(root, "Extract");
            var sourcePath = Path.Combine(root, "source.txt");
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(sourcePath, mode);

            var executable = Path.Combine(AppContext.BaseDirectory, "HephaestusWorkbench.AnalysisProcessFixture.exe");
            Assert.True(File.Exists(executable), $"未找到分析进程测试程序：{executable}");
            var manifest = new ExtensionManifest
            {
                SchemaVersion = 2,
                Id = "log-analyzer",
                Name = "日志分析",
                Version = "2.0.0",
                Kind = ExtensionKind.Analysis,
                PublisherId = "thelinyue",
                HostApiVersion = "1.0",
                MinHostVersion = "2.0.0",
                Runtime = new ExtensionRuntime
                {
                    Kind = ExtensionRuntimeKind.Process,
                    Protocol = AnalysisProcessProtocol.Version,
                    Entry = Path.GetFileName(executable)
                },
                Capabilities = ["analysis.engine"],
                Permissions = [],
                Dependencies = [],
                DirectoryPath = AppContext.BaseDirectory
            };
            var request = new AnalysisProcessRequest
            {
                Protocol = AnalysisProcessProtocol.Version,
                RequestId = Guid.NewGuid().ToString("N"),
                CaseId = "case-1",
                SourcePath = sourcePath,
                OutputDirectory = Path.Combine(extractDirectory, "Report"),
                ExtractDirectory = extractDirectory,
                Scope = AnalysisScope.Comprehensive
            };
            return new TestEnvironment(
                root,
                extractDirectory,
                new AnalysisProcessHost(new WorkbenchLogger(Path.Combine(root, "Logs"))),
                manifest,
                request);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
