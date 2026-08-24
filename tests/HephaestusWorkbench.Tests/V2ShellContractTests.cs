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
        Assert.Contains("BundledExtensionInitializationService", app);
        Assert.Contains("ExtensionCatalogClient", app);
        Assert.Contains("new TaskCenter(TasksRepository, Logger)", app);
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
    public void ProductionComposition_InitializesBundledExtensionsBeforeInboxAndMainViewModel()
    {
        var app = File.ReadAllText(Path.Combine(FindAppDirectory(), "App.xaml.cs"));
        var project = XDocument.Load(Path.Combine(FindAppDirectory(), "HephaestusWorkbench.App.csproj"));

        var extensionSettingsIndex = app.IndexOf("await ExtensionSettings.EnsureAsync()", StringComparison.Ordinal);
        var bundledIndex = app.IndexOf("await InitializeBundledExtensionsAsync()", StringComparison.Ordinal);
        var inboxIndex = app.IndexOf("await Inbox.StartAsync()", StringComparison.Ordinal);
        var mainViewModelIndex = app.IndexOf("MainViewModel = new MainViewModel", StringComparison.Ordinal);

        Assert.True(extensionSettingsIndex >= 0, "启动过程必须先初始化 extensions.json。");
        Assert.True(bundledIndex > extensionSettingsIndex, "内置扩展必须在 v2 配置完成后初始化。");
        Assert.True(inboxIndex > bundledIndex, "内置扩展失败必须在日志收件箱启动前阻止启动。");
        Assert.True(mainViewModelIndex > bundledIndex, "内置扩展失败必须在主界面 ViewModel 创建前阻止启动。");
        Assert.Contains("Path.Combine(AppContext.BaseDirectory, \"BundledExtensions\")", app, StringComparison.Ordinal);
        Assert.Contains("REQUIRE_BUNDLED_EXTENSIONS", app, StringComparison.Ordinal);

        var constantGroups = project.Root!.Elements("PropertyGroup")
            .Where(group => (group.Element("DefineConstants")?.Value ?? string.Empty)
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Contains("REQUIRE_BUNDLED_EXTENSIONS", StringComparer.Ordinal))
            .ToArray();
        var constantGroup = Assert.Single(constantGroups);
        Assert.Equal("'$(RequireBundledExtensions)' == 'true'", (string?)constantGroup.Attribute("Condition"));
    }

    [Fact]
    public void ProductionComposition_OwnsExtensionRefreshAndWorkspaceVersionLease()
    {
        var app = File.ReadAllText(Path.Combine(FindAppDirectory(), "App.xaml.cs"));
        var main = File.ReadAllText(Path.Combine(FindAppDirectory(), "ViewModels", "MainViewModel.cs"));

        Assert.Contains("LeaseCurrentVersion", app);
        Assert.Contains("new WorkspaceHostWindow(lease, Paths.CacheDirectory, Logger)", app);
        Assert.DoesNotContain("window.Closed", app);
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
    public void LegacyPluginUiFiles_AreRemoved()
    {
        var appDirectory = FindAppDirectory();
        var legacyFiles = new[]
        {
            Path.Combine("ViewModels", "MarketplacePluginsViewModel.cs"),
            Path.Combine("ViewModels", "PluginsViewModel.cs"),
            Path.Combine("Views", "MarketplacePluginsPage.xaml"),
            Path.Combine("Views", "MarketplacePluginsPage.xaml.cs"),
            Path.Combine("Views", "PluginsPage.xaml"),
            Path.Combine("Views", "PluginsPage.xaml.cs"),
            Path.Combine("Views", "WebToolWindow.xaml"),
            Path.Combine("Views", "WebToolWindow.xaml.cs")
        };

        // v2 Shell 只允许固定扩展中心和受控 Workspace Host，旧插件页面不得继续留在 App 中等待误用。
        Assert.All(legacyFiles, relativePath => Assert.False(
            File.Exists(Path.Combine(appDirectory, relativePath)),
            $"旧插件界面文件仍然存在：{relativePath}"));
    }

    [Fact]
    public void LegacyPluginSeedAssets_AreRemoved()
    {
        var appDirectory = FindAppDirectory();
        var project = File.ReadAllText(Path.Combine(appDirectory, "HephaestusWorkbench.App.csproj"));

        // v2 安装介质只允许 BundledExtensions；旧 PluginSeed 不得重新进入项目或发布目录。
        Assert.False(Directory.Exists(Path.Combine(appDirectory, "PluginSeed")));
        Assert.DoesNotContain("PluginSeed", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PluginBinaryPath", project, StringComparison.OrdinalIgnoreCase);
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
