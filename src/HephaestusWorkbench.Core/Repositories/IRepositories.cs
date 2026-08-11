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

public interface IReportSessionRepository
{
    Task<IReadOnlyList<ReportSession>> ListAsync(CancellationToken cancellationToken = default);
    Task ReplaceAsync(IReadOnlyList<ReportSession> sessions, CancellationToken cancellationToken = default);
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
