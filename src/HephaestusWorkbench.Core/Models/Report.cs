namespace HephaestusWorkbench.Core.Models;

/// <summary>标准化后的报告目录。</summary>
public sealed class Report
{
    public required string Id { get; init; }
    public required string CaseId { get; init; }
    public required string Path { get; init; }
    public string? PluginId { get; init; }
    public DateTime CreateTime { get; init; }
}
