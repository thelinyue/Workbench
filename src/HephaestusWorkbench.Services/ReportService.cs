using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Repositories;
using HephaestusWorkbench.Core.Services;

namespace HephaestusWorkbench.Services;

/// <summary>集中处理分析中心所需的报告查询。</summary>
public sealed class ReportService
{
    private readonly IReportRepository _reports;
    private readonly CaseAnalysisService _analysis;

    public ReportService(IReportRepository reports, CaseAnalysisService analysis)
    {
        _reports = reports;
        _analysis = analysis;
    }


    /// <summary>为分析中心创建统一的默认浏览器报告打开服务。</summary>
    public IReportOpenService CreateOpenService(WorkbenchLogger logger, IReportProcessLauncher? launcher = null, TimeProvider? timeProvider = null)
        => new ReportOpenService(_analysis, _reports, launcher ?? new WindowsReportProcessLauncher(), logger, timeProvider);
    public Task<IReadOnlyList<ReportSummary>> ListAsync(ReportQuery query, CancellationToken cancellationToken = default)
        => _reports.ListAsync(query, cancellationToken);

    public async Task<ReportSummary?> GetLatestForCaseAsync(string caseId, CancellationToken cancellationToken = default)
    {
        var reports = await _reports.ListAsync(new ReportQuery(), cancellationToken);
        return reports.FirstOrDefault(x => x.CaseId == caseId);
    }

    public Task<AnalysisCase?> GetCaseAsync(string caseId, CancellationToken cancellationToken = default)
        => _analysis.GetCaseAsync(caseId, cancellationToken);

    /// <summary>分析中心的生命周期删除仍由案例分析服务统一执行。</summary>
    public Task DeleteReportAndCaseAsync(ReportSummary report, CancellationToken cancellationToken = default)
        => DeleteReportLifecycleAsync(report.CaseId, cancellationToken);

    private async Task DeleteReportLifecycleAsync(string caseId, CancellationToken cancellationToken)
    {
        var item = await _analysis.GetCaseAsync(caseId, cancellationToken);
        if (item is not null) await _analysis.DeleteLifecycleAsync(item.SourcePath, cancellationToken);
    }
}
