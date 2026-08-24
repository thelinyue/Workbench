using System.Xml.Linq;

namespace HephaestusWorkbench.Tests;

/// <summary>
/// 锁定设置页的信息架构，确保工作空间与存储操作保持在受控设置路径中。
/// </summary>
public sealed class SettingsInformationArchitectureTests
{
    [Fact]
    public void SettingsPage_OrganizesWorkspaceMonitoringSshAndStoragePreferences()
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
        Assert.Contains(document.Descendants(), element => (string?)element.Attribute("Text") == "工作空间");
        Assert.Contains(document.Descendants(), element => (string?)element.Attribute("Text") == "日志监控");
        Assert.Contains(document.Descendants(), element => (string?)element.Attribute("Text") == "SSH 与终端");
        Assert.Contains(document.Descendants(), element => (string?)element.Attribute("Text") == "存储");
        Assert.Contains(document.Descendants(), element => (string?)element.Attribute("Text") == "外观");
        Assert.Contains(document.Descendants(), element => (string?)element.Attribute("Text") == "扩展策略");
        Assert.Contains(bindings, value => value.Contains("AutoCheckExtensionUpdates", StringComparison.Ordinal));
        Assert.Contains(document.Descendants(), element =>
            (string?)element.Attribute("AutomationProperties.Name") == "启动时自动检查扩展更新");
        Assert.Contains(bindings, value => value.Contains("CurrentDataRoot", StringComparison.Ordinal));
        Assert.Contains(bindings, value => value.Contains("OpenWorkspaceDirectoryCommand", StringComparison.Ordinal));
        Assert.Contains(bindings, value => value.Contains("CandidateDataRoot", StringComparison.Ordinal));
        Assert.Contains(bindings, value => value.Contains("RegisterDataRootChangeCommand", StringComparison.Ordinal));
        Assert.Contains(bindings, value => value.Contains("StorageFeedback", StringComparison.Ordinal));
        Assert.Contains(bindings, value => value.Contains("RestartApplicationCommand", StringComparison.Ordinal));
        Assert.DoesNotContain(document.Descendants(), element => (string?)element.Attribute("Text") == "报告");
        Assert.Contains(bindings, value => value.Contains("HasWatchDirectories", StringComparison.Ordinal));
        Assert.Contains(bindings, value => value.Contains("HasDirectoryFeedback", StringComparison.Ordinal));
        Assert.Contains(bindings, value => value.Contains("MessageIsError", StringComparison.Ordinal));
        Assert.Contains(document.Descendants(), element => (string?)element.Attribute("Text") == "尚未添加监控目录");
        Assert.DoesNotContain(document.Descendants(), element => (string?)element.Attribute("Text") == "同时打开的报告标签上限（1–10）");
        Assert.Contains(document.Descendants(), element => (string?)element.Attribute("LastChildFill") == "False");
    }

    [Fact]
    public void AnalysisPage_DoesNotOwnStorageCleanupSettings()
    {
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
