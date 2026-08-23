using System.Text.Json.Serialization;

namespace HephaestusWorkbench.Core.Models;

/// <summary>
/// 工作区配置，记录数据根目录和一个或多个日志监控目录。
/// 配置文件中的路径统一保存为绝对路径，避免工作目录变化导致监控失效。
/// </summary>
public sealed class WorkspaceConfig
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 2;

    public string DataPath { get; set; } = string.Empty;
    public List<string> MonitorPaths { get; set; } = new();
}

/// <summary>应用级偏好配置，不保存案例、报告等业务数据。</summary>
public sealed class AppSettingsConfig
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 2;

    public const string LightTheme = "Light";
    public const string DarkTheme = "Dark";

    public string Theme { get; set; } = LightTheme;
    public int CleanupRetentionDays { get; set; } = 7;

}
