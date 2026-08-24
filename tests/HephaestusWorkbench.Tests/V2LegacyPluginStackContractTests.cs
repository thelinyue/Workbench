namespace HephaestusWorkbench.Tests;

/// <summary>锁定 v2 正式版只保留 manifest/catalog v2 扩展栈，不再编译旧插件兼容契约。</summary>
public sealed class V2LegacyPluginStackContractTests
{
    [Fact]
    public void LegacyPluginServiceAndSdkFiles_AreRemoved()
    {
        var root = FindRepositoryRoot();
        var legacyFiles = new[]
        {
            Path.Combine("src", "HephaestusWorkbench.Services", "PluginCatalog.cs"),
            Path.Combine("src", "HephaestusWorkbench.Services", "PluginMarketplaceService.cs"),
            Path.Combine("src", "HephaestusWorkbench.Services", "PluginProvisioningService.cs"),
            Path.Combine("src", "HephaestusWorkbench.Services", "ProcessPluginRunners.cs"),
            Path.Combine("src", "HephaestusWorkbench.Services", "RuleDistributionService.cs"),
            Path.Combine("src", "HephaestusWorkbench.Services", "GitHubDownloadMirrorTemplate.cs"),
            Path.Combine("src", "HephaestusWorkbench.Services", "RulePackageVerifier.cs"),
            Path.Combine("src", "HephaestusWorkbench.Services", "RulePublisher.cs"),
            Path.Combine("src", "HephaestusWorkbench.Services", "GitHubRuleRepositoryService.cs"),
            Path.Combine("src", "HephaestusWorkbench.Services", "MaintainerSettingsStore.cs"),
            Path.Combine("src", "HephaestusWorkbench.Services", "MaintainerModeService.cs"),
            Path.Combine("src", "HephaestusWorkbench.Services", "DpapiSecretStore.cs"),
            Path.Combine("src", "HephaestusWorkbench.PluginSDK", "PluginContracts.cs")
        };

        Assert.All(legacyFiles, relativePath => Assert.False(
            File.Exists(Path.Combine(root, relativePath)),
            $"旧插件兼容文件仍然存在：{relativePath}"));
    }

    [Fact]
    public void CoreAndConfiguration_DoNotExposeLegacyPluginState()
    {
        var root = FindRepositoryRoot();
        var model = File.ReadAllText(Path.Combine(
            root, "src", "HephaestusWorkbench.Core", "Models", "WorkbenchConfiguration.cs"));
        var service = File.ReadAllText(Path.Combine(
            root, "src", "HephaestusWorkbench.Services", "WorkbenchConfigurationService.cs"));

        var dataPaths = File.ReadAllText(Path.Combine(
            root, "src", "HephaestusWorkbench.Data", "DataPaths.cs"));
        var settingsService = File.ReadAllText(Path.Combine(
            root, "src", "HephaestusWorkbench.Services", "SettingsService.cs"));
        var sdkProject = File.ReadAllText(Path.Combine(
            root, "src", "HephaestusWorkbench.PluginSDK", "HephaestusWorkbench.PluginSDK.csproj"));
        var ruleModels = File.ReadAllText(Path.Combine(
            root, "src", "HephaestusWorkbench.Core", "Models", "RuleModels.cs"));

        Assert.DoesNotContain("PluginConfig", model, StringComparison.Ordinal);
        Assert.DoesNotContain("PluginInstallSource", model, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsurePluginConfigAsync", service, StringComparison.Ordinal);
        Assert.DoesNotContain("SavePluginConfigAsync", service, StringComparison.Ordinal);
        Assert.DoesNotContain("UpsertPluginAsync", service, StringComparison.Ordinal);
        Assert.DoesNotContain("NormalizePluginConfig", service, StringComparison.Ordinal);
        Assert.DoesNotContain("MarketplaceCatalogCacheFile", dataPaths, StringComparison.Ordinal);
        Assert.DoesNotContain("GitHubDownloadMirrorTemplate", model, StringComparison.Ordinal);
        Assert.DoesNotContain("GitHubDownloadMirrorTemplate", settingsService, StringComparison.Ordinal);
        Assert.DoesNotContain("RulePublisherTokenFile", dataPaths, StringComparison.Ordinal);
        Assert.DoesNotContain("RuleCatalogEntry", ruleModels, StringComparison.Ordinal);
        Assert.DoesNotContain("RuleSyncResult", ruleModels, StringComparison.Ordinal);
        Assert.DoesNotContain("HephaestusWorkbench.Core.csproj", sdkProject, StringComparison.Ordinal);
    }

    [Fact]
    public void CurrentDocumentation_DescribesOnlyImplementedV2ExtensionContracts()
    {
        var root = FindRepositoryRoot();
        var pluginDevelopment = File.ReadAllText(Path.Combine(root, "docs", "plugin-development.md"));
        var moduleMap = File.ReadAllText(Path.Combine(root, "docs", "module_map.md"));
        var projectContext = File.ReadAllText(Path.Combine(root, ".codex", "project_context.md"));
        var distribution = File.ReadAllText(Path.Combine(root, "docs", "distribution.md"));
        var currentDocumentation = new[] { pluginDevelopment, moduleMap, projectContext, distribution };

        Assert.Contains("\"schemaVersion\": 2", pluginDevelopment, StringComparison.Ordinal);
        Assert.Contains("analysis-process-v1", pluginDevelopment, StringComparison.Ordinal);
        Assert.Contains("WorkspaceHostWindow", pluginDevelopment, StringComparison.Ordinal);
        Assert.All(currentDocumentation, documentation =>
        {
            Assert.DoesNotContain("PluginCatalog", documentation, StringComparison.Ordinal);
            Assert.DoesNotContain("PluginProvisioningService", documentation, StringComparison.Ordinal);
            Assert.DoesNotContain("LegacyLogAnalyzerRunner", documentation, StringComparison.Ordinal);
            Assert.DoesNotContain("StandardExePluginRunner", documentation, StringComparison.Ordinal);
            Assert.DoesNotContain("PluginContracts.cs", documentation, StringComparison.Ordinal);
            Assert.DoesNotContain("PluginSeed", documentation, StringComparison.Ordinal);
            Assert.DoesNotContain("PluginBinaryPath", documentation, StringComparison.Ordinal);
            Assert.DoesNotContain("plugin_info", documentation, StringComparison.Ordinal);
            Assert.DoesNotContain("plugins.json", documentation, StringComparison.Ordinal);
            Assert.DoesNotContain("legacy runner", documentation, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("report.html", documentation, StringComparison.OrdinalIgnoreCase);
        });

        Assert.Contains("没有 Analysis Content 的内容 DTO/schema", pluginDevelopment, StringComparison.Ordinal);
        Assert.Contains("Maintenance Content 已有预留", pluginDevelopment, StringComparison.Ordinal);
        Assert.Contains("v2 发布链路尚未完成", distribution, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalysisProcessDocumentation_MatchesStrictWireRequestFields()
    {
        var root = FindRepositoryRoot();
        var documents = new[]
        {
            File.ReadAllText(Path.Combine(root, "docs", "plugin-development.md")),
            File.ReadAllText(Path.Combine(root, "docs", "superpowers", "specs", "2026-08-23-analysis-scope-design.md"))
        };

        foreach (var documentation in documents)
        foreach (var field in new[]
                 {
                     "protocol",
                     "requestId",
                     "caseId",
                     "sourcePath",
                     "outputDirectory",
                     "extractDirectory",
                     "rulesPath",
                     "scope"
                 })
        {
            Assert.Contains($"`{field}`", documentation, StringComparison.Ordinal);
        }

        Assert.All(documents, documentation =>
        {
            Assert.DoesNotContain("`extractPath`", documentation, StringComparison.Ordinal);
            Assert.DoesNotContain("`reportDirectory`", documentation, StringComparison.Ordinal);
            Assert.DoesNotContain("`analysisScope`", documentation, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void AnalysisProcessDocumentation_MatchesStrictWireResponseAndReportOutput()
    {
        var root = FindRepositoryRoot();
        var documentation = File.ReadAllText(Path.Combine(root, "docs", "plugin-development.md"));

        foreach (var field in new[] { "protocol", "requestId", "succeeded", "errorCode", "errorMessage" })
            Assert.Contains($"`{field}`", documentation, StringComparison.Ordinal);

        Assert.DoesNotContain("诊断摘要", documentation, StringComparison.Ordinal);
        Assert.Contains("`outputDirectory/index.html`", documentation, StringComparison.Ordinal);
        Assert.Contains("`outputDirectory` 等于 `extractDirectory/Report`", documentation, StringComparison.Ordinal);
        Assert.Contains("失败响应必须同时包含 `errorCode` 和 `errorMessage`", documentation, StringComparison.Ordinal);
        Assert.Contains("非零退出码时，宿主仍优先解析", documentation, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HephaestusWorkbench.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
    }
}

