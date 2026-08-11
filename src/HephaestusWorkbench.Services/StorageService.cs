using HephaestusWorkbench.Core.Repositories;
using HephaestusWorkbench.Data;

namespace HephaestusWorkbench.Services;

public sealed record StorageSummary(long TotalBytes, long ReleasableBytes, int CaseCount, long LogBytes, long ExtractBytes, long ReportBytes);

/// <summary>只负责计算和执行数据清理，确认对话由 UI 层负责。</summary>
public sealed class StorageService
{
    private readonly DataPaths _paths;
    private readonly IAnalysisCaseRepository _cases;

    public StorageService(DataPaths paths, IAnalysisCaseRepository cases)
    {
        _paths = paths;
        _cases = cases;
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
        FileUtilities.DeleteDirectoryIfExists(item.ExtractPath);
        if (File.Exists(item.SourcePath)) File.Delete(item.SourcePath);
    }
}
