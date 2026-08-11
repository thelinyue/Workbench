using HephaestusWorkbench.Core.Repositories;
using HephaestusWorkbench.Data;

namespace HephaestusWorkbench.Services;

public sealed record StorageSummary(long TotalBytes, long ReleasableBytes, int CaseCount, long LogBytes, long ExtractBytes, long ReportBytes);

/// <summary>只负责计算和执行数据清理，确认对话由 UI 层负责。</summary>
public sealed class StorageService
{
    private readonly DataPaths _paths;
    private readonly IAnalysisCaseRepository _cases;
    private readonly WorkbenchLogger? _logger;

    public StorageService(DataPaths paths, IAnalysisCaseRepository cases, WorkbenchLogger? logger = null)
    {
        _paths = paths;
        _cases = cases;
        _logger = logger;
    }

    public async Task<StorageSummary> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        var cases = await _cases.ListAsync(cancellationToken);
        var total = FileUtilities.GetDirectorySize(_paths.Root);
        var logs = cases.Sum(x => FileUtilities.GetFileSize(x.SourcePath));
        var extracted = cases.Sum(x => FileUtilities.GetDirectorySize(x.ExtractPath));
        var reports = cases.Sum(x => string.IsNullOrWhiteSpace(x.ReportPath) ? 0 : FileUtilities.GetDirectorySize(x.ReportPath));
        return new StorageSummary(total, logs + extracted, cases.Count, logs, extracted, reports);
    }

    public async Task CleanCaseDataAsync(string caseId, CancellationToken cancellationToken = default)
    {
        var item = await _cases.GetAsync(caseId, cancellationToken) ?? throw new InvalidOperationException("案例不存在。");
        try
        {
            FileUtilities.DeleteCaseArtifacts(item, _paths, deleteReport: false);
        }
        catch (Exception ex)
        {
            _logger?.Error($"清理案例原始数据失败：{item.DisplayName}", ex);
            throw new InvalidOperationException($"清理案例原始数据失败：{ex.Message}", ex);
        }
    }
}
