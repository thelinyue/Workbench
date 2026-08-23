namespace HephaestusWorkbench.Core.Models;

/// <summary>报告中心使用的只读聚合信息，避免 UI 直接拼接案例、任务和文件状态。</summary>
public sealed class ReportSummary
{
    public required string Id { get; init; }
    public required string CaseId { get; init; }
    public required string CaseName { get; init; }
    public required string DeviceId { get; init; }
    public required string Path { get; init; }
    public required string ExtractPath { get; init; }
    public string ReportKey { get; init; } = "legacy";
    public string Title { get; init; } = "综合日志分析报告";
    public string Kind { get; init; } = "log-analysis";
    public string EntryFile { get; init; } = "report.html";
    public bool IsDefault { get; init; } = true;
    public string? PluginId { get; init; }
    public string PluginName { get; init; } = "未知插件";
    public DateTime CreateTime { get; init; }
    public bool IsAvailable { get; init; }
    public string ReportFile => System.IO.Path.Combine(Path, EntryFile);
}

/// <summary>报告中心查询条件；结束日期由仓储按次日零点前处理。</summary>
public sealed record ReportQuery(
    string? Keyword = null,
    string? DeviceId = null,
    string? PluginId = null,
    DateTime? StartDate = null,
    DateTime? EndDate = null);
