namespace HephaestusWorkbench.Core.Models;

/// <summary>一个可取消的后台插件分析任务。</summary>
public sealed class AnalysisTask
{
    public required string Id { get; init; }
    public required string CaseId { get; init; }
    public required string PluginId { get; init; }
    public AnalysisScope AnalysisScope { get; init; } = AnalysisScope.Comprehensive;
    public TaskStatus Status { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string? ReportPath { get; set; }
    public string? ErrorMessage { get; set; }
}
