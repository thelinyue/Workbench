namespace HephaestusWorkbench.Data;

/// <summary>
/// 统一管理程序目录和用户数据目录，避免分析插件把输出写回程序安装目录。
/// 案例文件的实际位置由监控目录和分析插件决定：源日志与同名解压目录位于监控目录，
/// 报告固定写入解压目录下的 Report；Cases 目录仅保存宿主管理的案例数据。
/// </summary>
public sealed class DataPaths
{
    public DataPaths(string root)
    {
        Root = Path.GetFullPath(root);
        DatabaseDirectory = Path.Combine(Root, "Database");
        CasesDirectory = Path.Combine(Root, "Cases");
        InboxDirectory = Path.Combine(Root, "Inbox");
        ExtensionsDirectory = Path.Combine(Root, "Extensions");
        RulesDirectory = Path.Combine(Root, "Rules");
        OfficialRulesDirectory = Path.Combine(RulesDirectory, "Official");
        OfficialRulesFile = Path.Combine(OfficialRulesDirectory, "main.json");
        LocalRulesDirectory = Path.Combine(RulesDirectory, "Local");
        LocalAdditionsFile = Path.Combine(LocalRulesDirectory, "additions.json");
        ActiveRulesDirectory = Path.Combine(RulesDirectory, "Active");
        ActiveRulesFile = Path.Combine(ActiveRulesDirectory, "active.json");
        RulesHistoryDirectory = Path.Combine(RulesDirectory, "History");
        RulesStateDirectory = Path.Combine(RulesDirectory, "State");
        RulesStateFile = Path.Combine(RulesStateDirectory, "rules-state.json");
        LogsDirectory = Path.Combine(Root, "Logs");
        TempDirectory = Path.Combine(Root, "Temp");
        CacheDirectory = Path.Combine(Root, "Cache");
        ConfigDirectory = Path.Combine(Root, "Config");
        DatabaseFile = Path.Combine(DatabaseDirectory, "workbench.db");
        AppSettingsFile = Path.Combine(ConfigDirectory, "appsettings.json");
        RulePublisherTokenFile = Path.Combine(ConfigDirectory, "rule-publisher.token");
        ExtensionsConfigFile = Path.Combine(ConfigDirectory, "extensions.json");
        WorkspaceConfigFile = Path.Combine(ConfigDirectory, "workspace.json");
        MarketplaceCatalogCacheFile = Path.Combine(CacheDirectory, "marketplace-catalog.json");
        ExtensionCatalogCacheFile = Path.Combine(CacheDirectory, "extension-catalog.json");
    }

    public string Root { get; }
    public string DatabaseDirectory { get; }
    public string DatabaseFile { get; }
    public string CasesDirectory { get; }
    public string InboxDirectory { get; }
    public string ExtensionsDirectory { get; }
    public string RulesDirectory { get; }
    public string OfficialRulesDirectory { get; }
    public string OfficialRulesFile { get; }
    public string LocalRulesDirectory { get; }
    public string LocalAdditionsFile { get; }
    public string ActiveRulesDirectory { get; }
    public string ActiveRulesFile { get; }
    public string RulesHistoryDirectory { get; }
    public string RulesStateDirectory { get; }
    public string RulesStateFile { get; }
    public string LogsDirectory { get; }
    public string TempDirectory { get; }
    public string CacheDirectory { get; }
    public string ConfigDirectory { get; }
    public string AppSettingsFile { get; }
    public string RulePublisherTokenFile { get; }
    public string ExtensionsConfigFile { get; }
    public string WorkspaceConfigFile { get; }
    public string MarketplaceCatalogCacheFile { get; }
    public string ExtensionCatalogCacheFile { get; }

    public string GetCaseDirectory(string caseId) => Path.Combine(CasesDirectory, caseId);
    public string GetCaseSourceDirectory(string caseId) => Path.Combine(GetCaseDirectory(caseId), "Source");
    public string GetCaseExtractDirectory(string caseId) => Path.Combine(GetCaseDirectory(caseId), "Extract");
    /// <summary>报告统一放在实际解压目录下，便于工程师用一个目录管理原始内容和分析结果。</summary>
    public string GetReportDirectory(string extractPath) => Path.Combine(Path.GetFullPath(extractPath), "Report");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DatabaseDirectory);
        Directory.CreateDirectory(CasesDirectory);
        Directory.CreateDirectory(InboxDirectory);
        Directory.CreateDirectory(ExtensionsDirectory);
        Directory.CreateDirectory(RulesDirectory);
        Directory.CreateDirectory(OfficialRulesDirectory);
        Directory.CreateDirectory(LocalRulesDirectory);
        Directory.CreateDirectory(ActiveRulesDirectory);
        Directory.CreateDirectory(RulesHistoryDirectory);
        Directory.CreateDirectory(RulesStateDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(TempDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(ConfigDirectory);
    }
}
