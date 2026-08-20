using HephaestusWorkbench.Core.Models;

namespace HephaestusWorkbench.Core.Repositories;

public interface IAnalysisCaseRepository
{
    Task<IReadOnlyList<AnalysisCase>> ListAsync(CancellationToken cancellationToken = default);
    Task<AnalysisCase?> GetAsync(string id, CancellationToken cancellationToken = default);
    Task InsertAsync(AnalysisCase item, CancellationToken cancellationToken = default);
    Task UpdateAsync(AnalysisCase item, CancellationToken cancellationToken = default);
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);
}

public interface IAnalysisTaskRepository
{
    Task<IReadOnlyList<AnalysisTask>> ListAsync(CancellationToken cancellationToken = default);
    Task<AnalysisTask?> GetAsync(string id, CancellationToken cancellationToken = default);
    Task InsertAsync(AnalysisTask item, CancellationToken cancellationToken = default);
    Task UpdateAsync(AnalysisTask item, CancellationToken cancellationToken = default);
}

public interface IReportRepository
{
    Task InsertAsync(Report item, CancellationToken cancellationToken = default);
    Task<Report?> GetAsync(string id, CancellationToken cancellationToken = default);
    Task<Report?> GetByCaseIdAsync(string caseId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReportSummary>> ListAsync(ReportQuery query, CancellationToken cancellationToken = default);
}


public interface IPluginInfoRepository
{
    Task UpsertAsync(PluginInfo item, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PluginInfo>> ListAsync(CancellationToken cancellationToken = default);
}

public interface ISettingsStore
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);
    Task SetAsync(string key, string value, CancellationToken cancellationToken = default);
}

/// <summary>
/// 将案例、任务和报告的关键状态转换集中到一个持久化边界，避免多次独立写入产生半成品记录。
/// </summary>
public interface IAnalysisLifecycleRepository
{
    Task CreateAsync(AnalysisCase analysisCase, AnalysisTask task, CancellationToken cancellationToken = default);
    Task MarkRunningAsync(AnalysisCase analysisCase, AnalysisTask task, CancellationToken cancellationToken = default);
    Task CompleteAsync(AnalysisCase analysisCase, AnalysisTask task, Report? report, CancellationToken cancellationToken = default);
    /// <summary>在一个事务中删除案例及其关联的报告会话、报告和分析任务记录。</summary>
    Task DeleteByCaseIdsAsync(IReadOnlyCollection<string> caseIds, CancellationToken cancellationToken = default);
    Task<int> RecoverInterruptedAsync(DateTime recoveredAt, CancellationToken cancellationToken = default);
}
