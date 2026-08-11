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

    /// <summary>产品约定：从报告中心删除报告等价于删除所属案例及其全部数据。</summary>
    public Task DeleteReportAndCaseAsync(ReportSummary report, CancellationToken cancellationToken = default)
        => _analysis.DeleteAsync(report.CaseId, cancellationToken);
}
