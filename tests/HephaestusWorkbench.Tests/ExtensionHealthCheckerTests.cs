using HephaestusWorkbench.PluginSDK;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

public sealed class ExtensionHealthCheckerTests
{
    [Fact]
    public async Task CheckAsync_ProcessEntryMissing_RejectsCandidate()
    {
        using var directory = new TemporaryDirectory();
        var checker = new ExtensionHealthChecker();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            checker.CheckAsync(ProcessManifest(directory.Path, "missing.exe")));

        Assert.Contains("入口文件不存在", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAsync_ProcessEntryExists_AcceptsCandidate()
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "analyzer.exe"), "fixture");
        var checker = new ExtensionHealthChecker();

        await checker.CheckAsync(ProcessManifest(directory.Path, "analyzer.exe"));
    }

    private static ExtensionManifest ProcessManifest(string directory, string entry) => new()
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
            Entry = entry
        },
        Capabilities = ["analysis.engine"],
        Permissions = [],
        Dependencies = [],
        DirectoryPath = directory
    };

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
