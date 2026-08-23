namespace HephaestusWorkbench.Core.Services;

/// <summary>
/// 由宿主安全打开分析报告。调用方只提供报告标识，不能自行指定 HTML 入口或任意文件路径。
/// </summary>
public interface IReportOpenService
{
    Task<ReportOpenResult> OpenAsync(ReportOpenRequest request, CancellationToken cancellationToken = default);
}

/// <summary>打开报告请求。报告目录与案例目录由宿主持久化数据解析。</summary>
public sealed record ReportOpenRequest(string ReportId);

/// <summary>打开报告结果，失败时返回可直接展示给用户的中文错误。</summary>
public sealed record ReportOpenResult(
    bool Success,
    string? ReportEntryPath,
    string? ErrorMessage,
    DateTime? OpenedAt = null);
