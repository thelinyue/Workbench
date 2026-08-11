namespace HephaestusWorkbench.Core.Models;

/// <summary>一个已打开报告 Tab 的持久化状态。</summary>
public sealed class ReportSession
{
    public required string Id { get; init; }
    public required string ReportId { get; init; }
    public int OrderIndex { get; init; }
    public bool IsActive { get; init; }
    public double ScrollPosition { get; init; }
    public DateTime LastOpenTime { get; init; }
}
