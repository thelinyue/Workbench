namespace HephaestusWorkbench.Core.Models;

/// <summary>
/// Analysis Case 是工作台的核心业务对象，保存一次完整日志分析所需的所有路径和状态。
/// </summary>
public sealed class AnalysisCase
{
    public required string Id { get; init; }
    public required string DisplayName { get; set; }
    public required string OriginalName { get; init; }
    public required string DeviceId { get; init; }
    public DateTime LogTime { get; init; }
    public CaseStatus Status { get; set; }
    public required string SourcePath { get; init; }
    public required string ExtractPath { get; init; }
    public string? ReportPath { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreateTime { get; init; }
    public DateTime UpdateTime { get; set; }
}
