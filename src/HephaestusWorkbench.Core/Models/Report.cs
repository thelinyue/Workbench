namespace HephaestusWorkbench.Core.Models;

/// <summary>标准化后的报告目录。</summary>
public sealed class Report
{
    public required string Id { get; init; }
    public required string CaseId { get; init; }
    public required string Path { get; init; }
    /// <summary>插件清单中的稳定报告键；旧报告统一迁移为 legacy。</summary>
    public string ReportKey { get; init; } = "legacy";
    public string Title { get; init; } = "综合日志分析报告";
    public string Kind { get; init; } = "log-analysis";
    public string EntryFile { get; init; } = "report.html";
    public bool IsDefault { get; init; } = true;
    public string? PluginId { get; init; }
    public string? PluginName { get; init; }
    public string? PluginVersion { get; init; }
    public DateTime CreateTime { get; init; }
}
