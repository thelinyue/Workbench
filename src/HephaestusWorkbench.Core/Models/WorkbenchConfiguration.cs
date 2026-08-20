namespace HephaestusWorkbench.Core.Models;

/// <summary>
/// 工作区配置，记录数据根目录和一个或多个日志监控目录。
/// 配置文件中的路径统一保存为绝对路径，避免工作目录变化导致监控失效。
/// </summary>
public sealed class WorkspaceConfig
{
    public string DataPath { get; set; } = string.Empty;
    public List<string> MonitorPaths { get; set; } = new();
}

/// <summary>应用级偏好配置，不保存案例、报告等业务数据。</summary>
public sealed class AppSettingsConfig
{
    public const string LightTheme = "Light";
    public const string DarkTheme = "Dark";

    public string Theme { get; set; } = LightTheme;
    public int MaxReportTabs { get; set; } = 10;
    public bool ManualCleanupEnabled { get; set; }
    public int CleanupRetentionDays { get; set; } = 7;

    /// <summary>
    /// GitHub 插件包的备用下载地址模板。留空表示只使用官方直连地址；模板中的
    /// {url} 会被替换为目录中的原始包地址，避免把加速策略写入单个插件配置。
    /// </summary>
    public string GitHubDownloadMirrorTemplate { get; set; } = string.Empty;
}

/// <summary>插件配置仅记录已登记插件和启用状态，插件文件仍由插件目录管理。</summary>
public sealed class PluginConfig
{
    public string? DefaultPluginId { get; set; }
    public List<PluginConfigEntry> Plugins { get; set; } = new();
}

public enum PluginInstallSource
{
    Manual,
    Bundled,
    Marketplace
}

public sealed class PluginConfigEntry
{
    public string Id { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public PluginInstallSource Source { get; set; } = PluginInstallSource.Manual;
}
