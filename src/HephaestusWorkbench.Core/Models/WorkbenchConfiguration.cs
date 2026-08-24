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
    public SshSettingsConfig Ssh { get; set; } = new();
    public TerminalSettingsConfig Terminal { get; set; } = new();
    public SshReconnectBehavior ReconnectBehavior { get; set; } = SshReconnectBehavior.AutomaticThreeAttempts;
    public ExtensionPolicyConfig Extension { get; set; } = new();

}

/// <summary>扩展中心的应用级更新偏好；扩展启用状态和更新通道仍由 extensions.json 管理。</summary>
public sealed class ExtensionPolicyConfig
{
    public bool AutoCheckUpdates { get; set; } = true;
}

/// <summary>SSH 连接默认值，不包含设备身份或任何凭据。</summary>
public sealed class SshSettingsConfig
{
    public int DefaultPort { get; set; } = 22;
}

/// <summary>内置 xterm.js 的显示偏好。</summary>
public sealed class TerminalSettingsConfig
{
    public string FontFamily { get; set; } = "Cascadia Mono";
    public double FontSize { get; set; } = 14;
}

/// <summary>交互终端在暂态断线后的有限重连策略。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SshReconnectBehavior
{
    Disabled,
    AutomaticThreeAttempts
}



/// <summary>设置页读取和保存的 SSH/终端偏好快照，不包含任何凭据。</summary>
public sealed record SshTerminalPreferences(
    int DefaultPort,
    string FontFamily,
    double FontSize,
    SshReconnectBehavior ReconnectBehavior);
