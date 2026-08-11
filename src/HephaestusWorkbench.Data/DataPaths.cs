namespace HephaestusWorkbench.Data;

/// <summary>
/// 统一管理程序目录和用户数据目录，避免分析插件把输出写回程序安装目录。
/// </summary>
public sealed class DataPaths
{
    public DataPaths(string root)
    {
        Root = Path.GetFullPath(root);
        DatabaseDirectory = Path.Combine(Root, "Database");
        CasesDirectory = Path.Combine(Root, "Cases");
        ReportsDirectory = Path.Combine(Root, "Reports");
        InboxDirectory = Path.Combine(Root, "Inbox");
        PluginsDirectory = Path.Combine(Root, "Plugins");
        LogsDirectory = Path.Combine(Root, "Logs");
        TempDirectory = Path.Combine(Root, "Temp");
        CacheDirectory = Path.Combine(Root, "Cache");
        ConfigDirectory = Path.Combine(Root, "Config");
        DatabaseFile = Path.Combine(DatabaseDirectory, "workbench.db");
        AppSettingsFile = Path.Combine(ConfigDirectory, "appsettings.json");
        PluginsConfigFile = Path.Combine(ConfigDirectory, "plugins.json");
        WorkspaceConfigFile = Path.Combine(ConfigDirectory, "workspace.json");
        MarketplaceCatalogCacheFile = Path.Combine(CacheDirectory, "marketplace-catalog.json");
    }

    public string Root { get; }
    public string DatabaseDirectory { get; }
    public string DatabaseFile { get; }
    public string CasesDirectory { get; }
    public string ReportsDirectory { get; }
    public string InboxDirectory { get; }
    public string PluginsDirectory { get; }
    public string LogsDirectory { get; }
    public string TempDirectory { get; }
    public string CacheDirectory { get; }
    public string ConfigDirectory { get; }
    public string AppSettingsFile { get; }
    public string PluginsConfigFile { get; }
    public string WorkspaceConfigFile { get; }
    public string MarketplaceCatalogCacheFile { get; }

    public string GetCaseDirectory(string caseId) => Path.Combine(CasesDirectory, caseId);
    public string GetCaseSourceDirectory(string caseId) => Path.Combine(GetCaseDirectory(caseId), "Source");
    public string GetCaseExtractDirectory(string caseId) => Path.Combine(GetCaseDirectory(caseId), "Extract");
    public string GetCaseReportDirectory(string caseId) => Path.Combine(GetCaseDirectory(caseId), "Report");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DatabaseDirectory);
        Directory.CreateDirectory(CasesDirectory);
        Directory.CreateDirectory(ReportsDirectory);
        Directory.CreateDirectory(InboxDirectory);
        Directory.CreateDirectory(PluginsDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(TempDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(ConfigDirectory);
    }
}
