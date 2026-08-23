using System.Xml.Linq;

namespace HephaestusWorkbench.Tests;

public sealed class AnalysisCenterXamlTests
{
    [Fact]
    public void AnalysisCenter_HasQuickAnalysisPendingAndHistoryWithoutEmbeddedReportViewer()
    {
        var document = LoadAnalysisCenterXaml();
        var text = document.ToString();

        Assert.Contains("快速分析", text);
        Assert.Contains("选择日志文件", text);
        Assert.Contains("待分析", text);
        Assert.Contains("历史记录", text);
        Assert.DoesNotContain("ReportTab", text);
        Assert.DoesNotContain("ReportViewerControl", text);
        Assert.DoesNotContain("ViewerHost", text);
        Assert.DoesNotContain("Reports.", text);
    }

    [Fact]
    public void AnalysisCenter_RemovesMetricsCleanupAndPluginSelection()
    {
        var text = LoadAnalysisCenterXaml().ToString();

        Assert.DoesNotContain("TotalSpace", text);
        Assert.DoesNotContain("ReleasableSpace", text);
        Assert.DoesNotContain("CleanupRetentionDays", text);
        Assert.DoesNotContain("存储管理", text);
        Assert.DoesNotContain("分析方式", text);
        Assert.DoesNotContain("选择插件", text);
        Assert.DoesNotContain("存储分析", text);
    }

    [Fact]
    public void AnalysisRows_UseSeparatePendingAndHistoryActions()
    {
        var document = LoadAnalysisCenterXaml();
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var pending = document.Descendants().Single(element => element.Attribute(x + "Key")?.Value == "PendingAnalysisRowTemplate").ToString();
        var history = document.Descendants().Single(element => element.Attribute(x + "Key")?.Value == "HistoryAnalysisRowTemplate").ToString();

        Assert.Contains("AnalyzeSingleCommand", pending);
        Assert.Contains("CancelAnalysisCommand", pending);
        Assert.Contains("OpenRowReportCommand", history);
        Assert.Contains("OpenExtractDirectoryCommand", history);
        Assert.Contains("更多", history);
        Assert.DoesNotContain("AnalyzeSingleCommand", history);
        Assert.DoesNotContain("CancelAnalysisCommand", history);
    }

    [Fact]
    public void QuickAnalysis_OnlyDeclaresSupportedTgzAndTgzTempFiles()
    {
        var xaml = LoadAnalysisCenterXaml().ToString();
        var codeBehind = LoadAnalysisCenterCodeBehind();
        var combined = xaml + codeBehind;

        Assert.Contains(".tgz", combined);
        Assert.Contains(".tgz.temp", combined);
        Assert.DoesNotContain(".tar.gz", combined);
        Assert.DoesNotContain(".zip", combined);
    }

    private static XDocument LoadAnalysisCenterXaml()
        => XDocument.Load(Path.Combine(FindRepositoryRoot(), "src", "HephaestusWorkbench.App", "Views", "AnalysisCenterPage.xaml"));

    private static string LoadAnalysisCenterCodeBehind()
        => File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "HephaestusWorkbench.App", "Views", "AnalysisCenterPage.xaml.cs"));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HephaestusWorkbench.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
