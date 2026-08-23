using System.Diagnostics;
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
    public async Task RunAsync_DoesNotTreatPreviousReportAsCurrentOutput()
    {
        using var environment = await TestEnvironment.CreateAsync("missing-report");
        var previousReport = Path.Combine(environment.ExtractDirectory, "Report", "index.html");
        Directory.CreateDirectory(Path.GetDirectoryName(previousReport)!);
        await File.WriteAllTextAsync(previousReport, "<html>previous</html>");

        var result = await environment.Host.RunAsync(environment.Manifest, environment.Request);

        Assert.False(result.Succeeded);
        Assert.Contains("未生成", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal("<html>previous</html>", await File.ReadAllTextAsync(previousReport));
    }

    [Fact]
    public async Task RunAsync_WhenNewRunFails_RestoresTheCompletePreviousReport()
    {
        using var environment = await TestEnvironment.CreateAsync("overwrite-then-fail");
        var reportDirectory = Path.Combine(environment.ExtractDirectory, "Report");
        Directory.CreateDirectory(reportDirectory);
        await File.WriteAllTextAsync(Path.Combine(reportDirectory, "index.html"), "<html>previous</html>");
        await File.WriteAllTextAsync(Path.Combine(reportDirectory, "asset.txt"), "old asset");

        var result = await environment.Host.RunAsync(environment.Manifest, environment.Request);

        Assert.False(result.Succeeded);
        Assert.Equal("<html>previous</html>", await File.ReadAllTextAsync(Path.Combine(reportDirectory, "index.html")));
        Assert.Equal("old asset", await File.ReadAllTextAsync(Path.Combine(reportDirectory, "asset.txt")));
    }

    [Fact]
    public async Task RunAsync_SerializesRunsThatTargetTheSameReportDirectory()
    {
        using var environment = await TestEnvironment.CreateAsync("delayed-failure");
        var failedTask = environment.Host.RunAsync(environment.Manifest, environment.Request);
        await WaitForFileAsync(Path.Combine(environment.ExtractDirectory, "fixture.started"));
        var successRequest = await environment.CreateRequestAsync("success", "request-success");

        var anotherHost = environment.CreateHost();
        var successTask = anotherHost.RunAsync(environment.Manifest, successRequest);
        var failed = await failedTask;
        var succeeded = await successTask;

        Assert.False(failed.Succeeded);
        Assert.True(succeeded.Succeeded, succeeded.ErrorMessage);
        Assert.True(File.Exists(Path.Combine(environment.ExtractDirectory, "Report", "index.html")));
    }

    [Fact]
    public async Task RunAsync_RejectsReportDirectoryJunctionBeforeChangingExternalFiles()
    {
        using var environment = await TestEnvironment.CreateAsync("success");
        var outside = Path.Combine(environment.Root, "OutsideReport");
        var reportDirectory = Path.Combine(environment.ExtractDirectory, "Report");
        Directory.CreateDirectory(environment.ExtractDirectory);
        Directory.CreateDirectory(outside);
        var outsideEntry = Path.Combine(outside, "index.html");
        await File.WriteAllTextAsync(outsideEntry, "outside");
        CreateJunction(reportDirectory, outside);
        try
        {
            var result = await environment.Host.RunAsync(environment.Manifest, environment.Request);

            Assert.False(result.Succeeded);
            Assert.Contains("文件系统链接", result.ErrorMessage, StringComparison.Ordinal);
            Assert.Equal("outside", await File.ReadAllTextAsync(outsideEntry));
        }
        finally
        {
            if (Directory.Exists(reportDirectory)) Directory.Delete(reportDirectory);
        }
    }

    [Fact]
    public async Task RunAsync_WhenExtensionReplacesReportRootWithFile_RestoresPreviousReport()
    {
        using var environment = await TestEnvironment.CreateAsync("report-root-file-failure");
        var reportDirectory = Path.Combine(environment.ExtractDirectory, "Report");
        Directory.CreateDirectory(reportDirectory);
        await File.WriteAllTextAsync(Path.Combine(reportDirectory, "index.html"), "previous");

        var result = await environment.Host.RunAsync(environment.Manifest, environment.Request);

        Assert.False(result.Succeeded);
        Assert.True(Directory.Exists(reportDirectory));
        Assert.Equal("previous", await File.ReadAllTextAsync(Path.Combine(reportDirectory, "index.html")));
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
    public async Task RunAsync_DrainsStandardOutputAndErrorConcurrently()
    {
        using var environment = await TestEnvironment.CreateAsync("dual-output");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var result = await environment.Host.RunAsync(environment.Manifest, environment.Request, timeout.Token);

        Assert.True(result.Succeeded, result.ErrorMessage);
    }

    [Fact]
    public async Task RunAsync_RejectsResponseThatDeclaresReportPath()
    {
        using var environment = await TestEnvironment.CreateAsync("report-path");

        var result = await environment.Host.RunAsync(environment.Manifest, environment.Request);

        Assert.False(result.Succeeded);
        Assert.Contains("JSON", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_CancellationStopsTheExtensionProcess()
    {
        using var environment = await TestEnvironment.CreateAsync("sleep-tree");
        using var cancellation = new CancellationTokenSource();

        var runTask = environment.Host.RunAsync(environment.Manifest, environment.Request, cancellation.Token);
        var parentMarker = Path.Combine(environment.ExtractDirectory, "fixture.started");
        var childMarker = Path.Combine(environment.ExtractDirectory, "fixture.child");
        await WaitForFileAsync(parentMarker);
        await WaitForFileAsync(childMarker);
        var parentProcessId = int.Parse(await File.ReadAllTextAsync(parentMarker));
        var childProcessId = int.Parse(await File.ReadAllTextAsync(childMarker));

        cancellation.Cancel();
        var result = await runTask;

        Assert.False(result.Succeeded);
        Assert.True(result.Cancelled);
        Assert.Contains("已取消", result.ErrorMessage, StringComparison.Ordinal);
        AssertProcessExited(parentProcessId);
        AssertProcessExited(childProcessId);
    }

    private static void CreateJunction(string linkDirectory, string targetDirectory)
    {
        using var junction = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/d /c mklink /J \"{linkDirectory}\" \"{targetDirectory}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        }) ?? throw new InvalidOperationException("无法启动 junction 测试进程。");
        junction.WaitForExit();
        Assert.True(junction.ExitCode == 0, junction.StandardError.ReadToEnd() + junction.StandardOutput.ReadToEnd());
    }

    private static async Task WaitForFileAsync(string path)
    {
        for (var attempt = 0; attempt < 100 && !File.Exists(path); attempt++)
            await Task.Delay(50);
        Assert.True(File.Exists(path), $"测试进程未创建启动标记：{path}");
    }

    private static void AssertProcessExited(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            Assert.True(process.HasExited, $"分析扩展进程 {processId} 在 RunAsync 返回时仍未退出。");
        }
        catch (ArgumentException)
        {
            // 进程对象已不存在，说明宿主在返回前完成了终止。
        }
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

        public AnalysisProcessHost CreateHost()
            => new(new WorkbenchLogger(Path.Combine(Root, "AdditionalHostLogs")));

        public async Task<AnalysisProcessRequest> CreateRequestAsync(string mode, string requestId)
        {
            var sourcePath = Path.Combine(Root, $"source-{requestId}.txt");
            await File.WriteAllTextAsync(sourcePath, mode);
            return new AnalysisProcessRequest
            {
                Protocol = AnalysisProcessProtocol.Version,
                RequestId = requestId,
                CaseId = Request.CaseId,
                SourcePath = sourcePath,
                OutputDirectory = Request.OutputDirectory,
                ExtractDirectory = Request.ExtractDirectory,
                Scope = AnalysisScope.Comprehensive
            };
        }

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
