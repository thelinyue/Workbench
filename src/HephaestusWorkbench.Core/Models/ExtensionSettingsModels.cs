using System.Text.Json.Serialization;

namespace HephaestusWorkbench.Core.Models;

/// <summary>
/// 扩展中心的宿主偏好配置。扩展的当前版本和健康状态以各扩展目录中的
/// current.json 为事实来源，此文档只保存启用状态与更新策略。
/// </summary>
public sealed class ExtensionSettingsDocument
{
    [JsonRequired]
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 2;

    [JsonRequired]
    [JsonPropertyName("updateChannel")]
    public string UpdateChannel { get; set; } = "stable";

    [JsonRequired]
    [JsonPropertyName("defaultAnalysisCapability")]
    public string DefaultAnalysisCapability { get; set; } = "analysis.engine";

    [JsonRequired]
    [JsonPropertyName("extensions")]
    public List<ExtensionEnablementEntry> Extensions { get; set; } = [];
}

/// <summary>
/// 单个扩展的本地启用偏好。这里故意不保存版本，避免与 current.json 形成双重事实来源。
/// </summary>
public sealed class ExtensionEnablementEntry
{
    [JsonRequired]
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonRequired]
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
}
