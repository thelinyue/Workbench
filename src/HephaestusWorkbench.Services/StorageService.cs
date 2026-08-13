using HephaestusWorkbench.Core.Repositories;
using HephaestusWorkbench.Data;

namespace HephaestusWorkbench.Services;

public sealed record StorageSummary(long TotalBytes, long ReleasableBytes, int CaseCount, long LogBytes, long ExtractBytes, long ReportBytes);

/// <summary>只负责计算存储摘要，生命周期删除统一由 CaseAnalysisService 执行。</summary>
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
        var onReadFailure = (string path, Exception ex) =>
            _logger?.Error($"计算存储占用时跳过无法读取的文件：{path}", ex);
        var total = FileUtilities.GetDirectorySize(_paths.Root, onReadFailure);
        var logs = cases.Sum(x => FileUtilities.GetFileSize(x.SourcePath, onReadFailure));
        var reportPaths = cases
            .Select(x => FileUtilities.GetReportDirectory(x.ExtractPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var extracted = cases.Sum(x => FileUtilities.GetDirectorySizeExcluding(x.ExtractPath, reportPaths, onReadFailure));
        var reports = reportPaths.Sum(path => FileUtilities.GetDirectorySize(path, onReadFailure));
        return new StorageSummary(total, logs + extracted + reports, cases.Count, logs, extracted, reports);
    }
}
