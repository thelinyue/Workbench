using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Repositories;

namespace HephaestusWorkbench.Services;

/// <summary>集中处理报告查询、会话持久化和删除语义，避免 UI 直接操作数据库。</summary>
public sealed class ReportService
{
    private readonly IReportRepository _reports;
    private readonly IReportSessionRepository _sessions;
    private readonly CaseAnalysisService _analysis;

    public ReportService(IReportRepository reports, IReportSessionRepository sessions, CaseAnalysisService analysis)
    {
        _reports = reports;
        _sessions = sessions;
        _analysis = analysis;
    }

    public Task<IReadOnlyList<ReportSummary>> ListAsync(ReportQuery query, CancellationToken cancellationToken = default)
        => _reports.ListAsync(query, cancellationToken);

    /// <summary>
    /// 返回报告库的当前报告视图。同一源日志重新分析时会产生新的历史案例，
    /// 但报告库只展示同一原始日志下最新的一份，避免每次重新分析都增加一行重复记录。
    /// </summary>
    public async Task<IReadOnlyList<ReportSummary>> ListLibraryAsync(ReportQuery query, CancellationToken cancellationToken = default)
    {
        var reports = await _reports.ListAsync(query, cancellationToken);
        var cases = await _analysis.ListCasesAsync(cancellationToken);
        var sourceByCaseId = cases.ToDictionary(x => x.Id, x => NormalizePath(x.SourcePath), StringComparer.OrdinalIgnoreCase);
        return reports
            .GroupBy(x => sourceByCaseId.TryGetValue(x.CaseId, out var sourcePath)
                ? sourcePath
                : NormalizePath(x.ExtractPath), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(x => x.CreateTime).First())
            .OrderByDescending(x => x.CreateTime)
            .ToArray();
    }

    public async Task<ReportSummary?> GetSummaryAsync(string reportId, CancellationToken cancellationToken = default)
        => (await _reports.ListAsync(new ReportQuery(), cancellationToken)).FirstOrDefault(x => x.Id == reportId);

    public async Task<ReportSummary?> GetLatestForCaseAsync(string caseId, CancellationToken cancellationToken = default)
        => (await _reports.ListAsync(new ReportQuery(), cancellationToken)).FirstOrDefault(x => x.CaseId == caseId);

    public Task<AnalysisCase?> GetCaseAsync(string caseId, CancellationToken cancellationToken = default)
        => _analysis.GetCaseAsync(caseId, cancellationToken);

    public Task<IReadOnlyList<ReportSession>> LoadSessionAsync(CancellationToken cancellationToken = default)
        => _sessions.ListAsync(cancellationToken);

    public Task SaveSessionAsync(IReadOnlyList<ReportSession> sessions, CancellationToken cancellationToken = default)
        => _sessions.ReplaceAsync(sessions, cancellationToken);

    private static string NormalizePath(string path)
        => string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : Path.GetFullPath(path.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>从报告中心删除报告等价于删除其源日志的完整生命周期。</summary>
    public Task DeleteReportAndCaseAsync(ReportSummary report, CancellationToken cancellationToken = default)
        => DeleteReportLifecycleAsync(report.CaseId, cancellationToken);

    private async Task DeleteReportLifecycleAsync(string caseId, CancellationToken cancellationToken)
    {
        var item = await _analysis.GetCaseAsync(caseId, cancellationToken);
        if (item is not null) await _analysis.DeleteLifecycleAsync(item.SourcePath, cancellationToken);
    }
}
