using System.Xml.Linq;

namespace HephaestusWorkbench.Tests;

/// <summary>
/// 锁定设置页的信息架构边界，避免低频配置迁回基础设置页并重新制造大面积留白。
/// </summary>
public sealed class SettingsInformationArchitectureTests
{
    [Fact]
    public void SettingsPage_KeepsOnlyCorePreferences()
    {
        var document = LoadXaml("SettingsPage.xaml");
        var bindings = document.Descendants()
            .Attributes()
            .Select(attribute => attribute.Value)
            .ToArray();

        Assert.Contains(bindings, value => value.Contains("WatchDirectories", StringComparison.Ordinal));
        Assert.Contains(bindings, value => value.Contains("SelectedTheme", StringComparison.Ordinal));
        Assert.DoesNotContain(bindings, value => value.Contains("MaxOpenReports", StringComparison.Ordinal));
        Assert.Contains(bindings, value => value.Contains("SaveCommand", StringComparison.Ordinal));
        Assert.DoesNotContain(bindings, value => value.Contains("CleanupEnabled", StringComparison.Ordinal));
        Assert.DoesNotContain(bindings, value => value.Contains("CleanupRetentionDays", StringComparison.Ordinal));
        Assert.DoesNotContain(bindings, value => value.Contains("GitHubDownloadMirror", StringComparison.Ordinal));
        Assert.Contains(document.Descendants(), element => (string?)element.Attribute("VerticalAlignment") == "Top");
        Assert.Contains(document.Descendants(), element => (string?)element.Attribute("Text") == "外观");
        Assert.DoesNotContain(document.Descendants(), element => (string?)element.Attribute("Text") == "报告");
        Assert.Contains(bindings, value => value.Contains("HasWatchDirectories", StringComparison.Ordinal));
        Assert.Contains(bindings, value => value.Contains("HasDirectoryFeedback", StringComparison.Ordinal));
        Assert.Contains(bindings, value => value.Contains("MessageIsError", StringComparison.Ordinal));
        Assert.Contains(document.Descendants(), element => (string?)element.Attribute("Text") == "尚未添加监控目录");
        Assert.DoesNotContain(document.Descendants(), element => (string?)element.Attribute("Text") == "同时打开的报告标签上限（1–10）");
        Assert.Contains(document.Descendants(), element => (string?)element.Attribute("LastChildFill") == "False");
    }

    [Fact]
    public void ContextPages_ExposeTheirOwnAdvancedSettingsSaveActions()
    {
        var marketplace = LoadXaml("MarketplacePluginsPage.xaml");
        var marketplaceBindings = marketplace.Descendants()
            .Attributes()
            .Select(attribute => attribute.Value)
            .ToArray();
        Assert.Contains(marketplaceBindings, value => value.Contains("GitHubDownloadMirrorTemplate", StringComparison.Ordinal));
        Assert.Contains(marketplaceBindings, value => value.Contains("SaveDownloadSettingsCommand", StringComparison.Ordinal));
        Assert.Contains(marketplace.Descendants(), element => (string?)element.Attribute("Header") == "下载设置");

        var analysis = LoadXaml("AnalysisCenterPage.xaml");
        var analysisBindings = analysis.Descendants()
            .Attributes()
            .Select(attribute => attribute.Value)
            .ToArray();
        Assert.DoesNotContain(analysisBindings, value => value.Contains("CleanupEnabled", StringComparison.Ordinal));
        Assert.DoesNotContain(analysisBindings, value => value.Contains("CleanupRetentionDays", StringComparison.Ordinal));
        Assert.DoesNotContain(analysisBindings, value => value.Contains("SaveCleanupSettingsCommand", StringComparison.Ordinal));
    }

    private static XDocument LoadXaml(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HephaestusWorkbench.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return XDocument.Load(Path.Combine(directory!.FullName, "src", "HephaestusWorkbench.App", "Views", fileName));
    }
}
