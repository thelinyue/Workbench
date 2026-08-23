using HephaestusWorkbench.PluginSDK;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

public sealed class PluginReportProtocolTests
{
    [Fact]
    public void PluginExecutionResult_KeepsLegacyReportPathAndAcceptsReportArtifacts()
    {
        var reports = new[]
        {
            new PluginReportArtifact("storage-health", "Linux 存储健康诊断报告", "storage-health", "storage-health-report.html", true)
        };

        var result = new PluginExecutionResult(0, "report", null, Reports: reports);

        Assert.Equal("report", result.ReportPath);
        Assert.Same(reports, result.Reports);
    }

    [Fact]
    public async Task DiscoverAsync_ParsesValidManifestAndResolvesTwoReports()
    {
        var root = CreateRoot();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "storage-health-report.html"), "<html></html>");
            await File.WriteAllTextAsync(Path.Combine(root, "log-analysis-report.html"), "<html></html>");
            await File.WriteAllTextAsync(Path.Combine(root, "reports.json"), """
                {
                  "schemaVersion": 1,
                  "reports": [
                    { "id": "storage-health", "title": "Linux 存储健康诊断报告", "kind": "storage-health", "file": "storage-health-report.html", "isDefault": true },
                    { "id": "log-analysis", "title": "综合日志分析报告", "kind": "log-analysis", "file": "log-analysis-report.html", "isDefault": false }
                  ]
                }
                """);

            var discovery = await PluginReportManifestReader.DiscoverAsync(root);

            Assert.True(discovery.ManifestExists);
            Assert.Null(discovery.ErrorMessage);
            Assert.Collection(discovery.Reports,
                report =>
                {
                    Assert.Equal("storage-health", report.Id);
                    Assert.True(report.IsDefault);
                },
                report => Assert.Equal("log-analysis-report.html", report.File));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task DiscoverAsync_WithoutManifestKeepsLegacyReportHtmlFallback()
    {
        var root = CreateRoot();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "report.html"), "<html></html>");

            var discovery = await PluginReportManifestReader.DiscoverAsync(root);

            Assert.False(discovery.ManifestExists);
            Assert.Null(discovery.ErrorMessage);
            Assert.Empty(discovery.Reports);
            Assert.True(discovery.LegacyReportExists);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Theory]
    [InlineData("{ \"schemaVersion\": 2, \"reports\": [] }")]
    [InlineData("{ \"schemaVersion\": 1, \"reports\": [{ \"id\": \"same\", \"title\": \"A\", \"kind\": \"a\", \"file\": \"a.html\", \"isDefault\": true }, { \"id\": \"same\", \"title\": \"B\", \"kind\": \"b\", \"file\": \"b.html\", \"isDefault\": false }] }")]
    [InlineData("{ \"schemaVersion\": 1, \"reports\": [{ \"id\": \"a\", \"title\": \"A\", \"kind\": \"a\", \"file\": \"missing.html\", \"isDefault\": true }] }")]
    [InlineData("{ \"schemaVersion\": 1, \"reports\": [{ \"id\": \" \", \"title\": \"A\", \"kind\": \"a\", \"file\": \"a.html\", \"isDefault\": true }] }")]
    [InlineData("{ \"schemaVersion\": 1, \"reports\": [{ \"id\": \"a\", \"title\": \"A\", \"kind\": \"a\", \"file\": \"a.html\", \"isDefault\": true }, { \"id\": \"b\", \"title\": \"B\", \"kind\": \"b\", \"file\": \"b.html\", \"isDefault\": true }] }")]
    [InlineData("not-json")]
    [InlineData("{ \"schemaVersion\": 1, \"reports\": [{ \"id\": \"a\", \"title\": \"A\", \"kind\": \"a\", \"file\": \"bad\\u0000.html\", \"isDefault\": true }] }")]
    [InlineData("{ \"schemaVersion\": 1, \"reports\": [{ \"id\": \"a\", \"title\": \"A\", \"kind\": \"a\", \"file\": \"../outside.html\", \"isDefault\": true }] }")]
    [InlineData("{ \"schemaVersion\": 1, \"reports\": [{ \"id\": \"a\", \"title\": \"A\", \"kind\": \"a\", \"file\": \"a.txt\", \"isDefault\": true }] }")]
    [InlineData("{ \"schemaVersion\": 1, \"reports\": [{ \"id\": \"a\", \"title\": \"A\", \"kind\": \"a\", \"file\": \"a.html\", \"isDefault\": false }] }")]
    public async Task DiscoverAsync_InvalidManifestReturnsChineseErrorAndNeverFallsBack(string json)
    {
        var root = CreateRoot();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "report.html"), "<html>legacy</html>");
            await File.WriteAllTextAsync(Path.Combine(root, "a.html"), "<html></html>");
            await File.WriteAllTextAsync(Path.Combine(root, "b.html"), "<html></html>");
            await File.WriteAllTextAsync(Path.Combine(root, "reports.json"), json);

            var discovery = await PluginReportManifestReader.DiscoverAsync(root);

            Assert.True(discovery.ManifestExists);
            Assert.NotNull(discovery.ErrorMessage);
            Assert.Contains("报告清单", discovery.ErrorMessage);
            Assert.Empty(discovery.Reports);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

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
