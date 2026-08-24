using System.Xml.Linq;

namespace HephaestusWorkbench.Tests;

public sealed class CommandDeckShellTests
{
    [Fact]
    public void MainWindow_ContainsFixedGroupedNavigationAndGlobalStatus()
    {
        var text = LoadAppFile("MainWindow.xaml").ToString();
        Assert.Contains("NavigationSections", text);
        Assert.Contains("StatusMessage", text);
        Assert.DoesNotContain("TaskPanel", text);
    }

    [Fact]
    public void AnalysisCenter_UsesQuickAnalysisAndExternalReports()
    {
        var text = LoadAppFile("Views\\AnalysisCenterPage.xaml").ToString();
        Assert.Contains("快速分析", text);
        Assert.Contains("待分析", text);
        Assert.Contains("历史记录", text);
        Assert.Contains("RefreshCommand", text);
        Assert.DoesNotContain("CleanupExpiredCommand", text);
        Assert.DoesNotContain("OpenTabs", text);
        Assert.DoesNotContain("ViewerHost", text);
    }

    [Fact]
    public void Themes_ExposeTheSameResourceKeys()
    {
        var light = LoadAppFile("Themes\\LightTheme.xaml");
        var dark = LoadAppFile("Themes\\DarkTheme.xaml");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        Assert.Equal(
            light.Descendants().Attributes(x + "Key").Select(a => a.Value).OrderBy(x => x),
            dark.Descendants().Attributes(x + "Key").Select(a => a.Value).OrderBy(x => x));
    }

    private static XDocument LoadAppFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HephaestusWorkbench.sln"))) directory = directory.Parent;
        Assert.NotNull(directory);
        return XDocument.Load(Path.Combine(directory!.FullName, "src", "HephaestusWorkbench.App", relativePath));
    }
}
