using System.Xml.Linq;
using HephaestusWorkbench.App.ViewModels;

namespace HephaestusWorkbench.Tests;

/// <summary>锁定 v2.0.0 固定信息架构，防止首页、任务弹窗或动态扩展导航重新进入 Shell。</summary>
public sealed class V2ShellContractTests
{
    [Fact]
    public void ShellNavigation_ContainsOnlyFrozenGroupsAndPages()
    {
        var sections = ShellNavigation.CreateFixed();

        Assert.Equal(new[] { "工作", "扩展", "系统" }, sections.Select(x => x.Title));
        Assert.Equal(new[] { "analysis", "ssh", "extensions", "settings" }, sections.SelectMany(x => x.Items).Select(x => x.Key));
        Assert.Equal(new[] { "分析中心", "SSH 终端", "扩展中心", "设置" }, sections.SelectMany(x => x.Items).Select(x => x.Title));
    }

    [Fact]
    public void MainWindow_HasGroupedNavigationWithoutTaskPopup()
    {
        var document = LoadAppFile("MainWindow.xaml");
        var text = document.ToString();

        Assert.Contains("NavigationSections", text);
        Assert.DoesNotContain("TaskPanel", text);
        Assert.DoesNotContain("Popup", text);
        Assert.DoesNotContain("本地运行", text);
    }

    [Fact]
    public void AppTemplates_ExcludeDashboardAndIncludeSshTerminal()
    {
        var text = LoadAppFile("App.xaml").ToString();

        Assert.DoesNotContain("DashboardViewModel", text);
        Assert.Contains("SshTerminalViewModel", text);
        Assert.Contains("ExtensionCenterViewModel", text);
        Assert.DoesNotContain("MarketplacePluginsViewModel", text);
    }

    [Fact]
    public void ProductionComposition_UsesOnlyV2ExtensionStack()
    {
        var app = File.ReadAllText(Path.Combine(FindAppDirectory(), "App.xaml.cs"));
        var main = File.ReadAllText(Path.Combine(FindAppDirectory(), "ViewModels", "MainViewModel.cs"));

        Assert.Contains("ExtensionCenterService", app);
        Assert.Contains("ExtensionSettingsStore", app);
        Assert.Contains("ExtensionInstaller", app);
        Assert.Contains("ExtensionCatalogClient", app);
        Assert.DoesNotContain("EnsurePluginConfigAsync", app);
        Assert.DoesNotContain("PluginProvisioningService", app);
        Assert.DoesNotContain("PluginMarketplaceService", app);
        Assert.DoesNotContain("PluginCatalog", app);
        Assert.DoesNotContain("RuleDistributionService", app);
        Assert.DoesNotContain("StorageService", app);
        Assert.Contains("ExtensionCenterViewModel", main);
        Assert.DoesNotContain("MarketplacePluginsViewModel", main);
        Assert.DoesNotContain("DefaultPluginId", main);
    }

    [Fact]
    public void ProductionComposition_OwnsExtensionRefreshAndWorkspaceVersionLease()
    {
        var app = File.ReadAllText(Path.Combine(FindAppDirectory(), "App.xaml.cs"));
        var main = File.ReadAllText(Path.Combine(FindAppDirectory(), "ViewModels", "MainViewModel.cs"));

        Assert.Contains("LeaseCurrentVersion", app);
        Assert.Contains("window.Closed", app);
        Assert.Contains("lease.Dispose", app);
        Assert.Contains("CancellationTokenSource", main);
        Assert.Contains(".Cancel()", main);
        Assert.Contains("logger.MessageWritten -= OnLogMessage", main);
    }

    [Fact]
    public void AnalysisCenter_DoesNotDependOnEmbeddedReportWorkspace()
    {
        var parameterTypes = typeof(AnalysisCenterViewModel)
            .GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.DoesNotContain(parameterTypes, type => type.Name == "ReportsWorkspaceViewModel");
        Assert.Null(typeof(AnalysisCenterViewModel).GetProperty("Reports"));
    }

    [Fact]
    public void BundledAnalysisManifest_DoesNotDeclareLegacyReportEntry()
    {
        var manifest = File.ReadAllText(Path.Combine(FindAppDirectory(), "PluginSeed", "manifest.json"));

        Assert.DoesNotContain("report.html", manifest, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reportPath", manifest, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PublishedPluginDocumentation_DoesNotDeclareLegacyReportEntry()
    {
        var documentation = string.Join("\n", Directory.EnumerateFiles(Path.Combine(FindRepositoryRoot(), "docs"), "*", SearchOption.AllDirectories)
            .Select(File.ReadAllText));

        Assert.DoesNotContain("report.html", documentation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reportPath", documentation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("报告查看器", documentation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LegacyReportWorkspaceFiles_AreRemoved()
    {
        var appDirectory = FindAppDirectory();

        Assert.False(File.Exists(Path.Combine(appDirectory, "ViewModels", "ReportsWorkspaceViewModel.cs")));
        Assert.False(File.Exists(Path.Combine(appDirectory, "ViewModels", "ReportTabViewModel.cs")));
        Assert.False(File.Exists(Path.Combine(appDirectory, "Views", "ReportViewerControl.xaml")));
    }

    private static XDocument LoadAppFile(string relativePath)
        => XDocument.Load(Path.Combine(FindAppDirectory(), relativePath));

    private static string FindAppDirectory()
        => Path.Combine(FindRepositoryRoot(), "src", "HephaestusWorkbench.App");

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HephaestusWorkbench.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
