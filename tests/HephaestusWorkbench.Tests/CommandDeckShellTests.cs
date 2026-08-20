using System.Xml.Linq;

namespace HephaestusWorkbench.Tests;

/// <summary>
/// 锁定 Command Deck 的壳层结构、双主题资源对齐和报告单宿主边界，避免后续页面迭代重新引入重复导航或查看器。
/// </summary>
public sealed class CommandDeckShellTests
{
    [Fact]
    public void MainWindow_ContainsCommandDeckNavigationAndGlobalStatusRegions()
    {
        var document = LoadAppFile("MainWindow.xaml");
        var text = document.ToString();

        Assert.Contains("HEPHAESTUS", text);
        Assert.Contains("WORKBENCH", text);
        var attributes = document.Descendants().Attributes().Select(attribute => attribute.Value).ToArray();
        Assert.Contains(attributes, value => value.Contains("NavigationItems", StringComparison.Ordinal));
        Assert.Contains(attributes, value => value.Contains("TaskPanel.ToggleCommand", StringComparison.Ordinal));
        Assert.Contains(attributes, value => value.Contains("StatusMessage", StringComparison.Ordinal));
        Assert.Contains(document.Descendants(), element => (string?)element.Attribute("Text") == "本地运行");
        Assert.Contains(document.Descendants(), element => (string?)element.Attribute("AutomationProperties.Name") == "后台任务");
    }

    [Fact]
    public void AnalysisCenter_DoesNotEmbedASecondReportPage()
    {
        var document = LoadAppFile("Views\\AnalysisCenterPage.xaml");

        Assert.DoesNotContain(document.Descendants(), element => element.Name.LocalName == "ReportPage");
    }

    [Fact]
    public void Themes_ExposeTheSameResourceKeys()
    {
        var light = LoadAppFile("Themes\\LightTheme.xaml");
        var dark = LoadAppFile("Themes\\DarkTheme.xaml");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var lightKeys = light.Descendants()
            .Attributes(x + "Key")
            .Select(attribute => attribute.Value)
            .OrderBy(value => value)
            .ToArray();
        var darkKeys = dark.Descendants()
            .Attributes(x + "Key")
            .Select(attribute => attribute.Value)
            .OrderBy(value => value)
            .ToArray();

        Assert.Equal(lightKeys, darkKeys);
    }

    [Fact]
    public void App_DoesNotRegisterStandaloneReportPageTemplate()
    {
        var document = LoadAppFile("App.xaml");
        var text = document.ToString();

        Assert.DoesNotContain("ReportPage", text);
        Assert.DoesNotContain("ReportsWorkspaceViewModel", text);
    }

    [Fact]
    public void Dashboard_UsesPrimaryActionTaskRailAndDataWorkspaces()
    {
        var document = LoadAppFile("Views\\DashboardPage.xaml");
        var text = document.ToString();

        Assert.Contains("DashboardWorkspace", text);
        Assert.Contains("DashboardDataWorkspace", text);
        Assert.Contains("DashboardMetricsStrip", text);
        Assert.Contains("CanStartQuickAnalysis", text);
        Assert.Contains("OpenTaskPanelCommand", text);
        Assert.Contains("RecentLogs", text);
        Assert.Contains("RecentCases", text);
        Assert.Contains("CurrentTasks", text);
    }

    [Fact]
    public void AnalysisCenter_UsesToolbarMetricsAndTableRegions()
    {
        var document = LoadAppFile("Views\\AnalysisCenterPage.xaml");
        var text = document.ToString();

        Assert.Contains("AnalysisListHost", text);
        Assert.Contains("AnalysisMetricsStrip", text);
        Assert.Contains("TableHeaderStyle", text);
        Assert.Contains("AnalyzeAllPendingCommand", text);
        Assert.Contains("RefreshCommand", text);
        Assert.Contains("AnalyzeSingleCommand", text);
        Assert.DoesNotContain("ToggleHistoryCommand", text);
        Assert.Contains("CleanupExpiredCommand", text);
    }

    [Fact]
    public void AnalysisCenter_EmbedsReportTabsAndSingleViewerHost()
    {
        var document = LoadAppFile("Views\\AnalysisCenterPage.xaml");
        var text = document.ToString();
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        Assert.Contains("ReportTabStrip", text);
        Assert.Contains("OpenTabs", text);
        Assert.Contains("ViewerHost", text);
        Assert.Contains("OpenSelectedExtractDirectoryCommand", text);
        Assert.Contains("ShowAnalysisListCommand", text);
        Assert.Contains("返回分析中心", text);
        Assert.Contains("IsAnalysisListVisible", text);
        Assert.Equal(1, document.Descendants().Count(element => (string?)element.Attribute(x + "Name") == "ViewerHost"));
    }

    private static XDocument LoadAppFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HephaestusWorkbench.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return XDocument.Load(Path.Combine(directory!.FullName, "src", "HephaestusWorkbench.App", relativePath));
    }
}
