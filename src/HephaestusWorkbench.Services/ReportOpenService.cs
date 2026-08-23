using System.Diagnostics;
using HephaestusWorkbench.Core.Repositories;
using HephaestusWorkbench.Core.Services;

namespace HephaestusWorkbench.Services;

/// <summary>隔离 Windows 进程启动，便于测试浏览器启动失败且不真实打开窗口。</summary>
public interface IReportProcessLauncher
{
    void Open(string reportEntryPath);
}

/// <summary>使用 Windows 默认浏览器打开本地报告入口。</summary>
public sealed class WindowsReportProcessLauncher : IReportProcessLauncher
{
    public void Open(string reportEntryPath)
    {
        _ = Process.Start(new ProcessStartInfo
        {
            FileName = reportEntryPath,
            UseShellExecute = true
        }) ?? throw new InvalidOperationException("Windows 未能启动默认浏览器。");
    }
}

/// <summary>检查报告路径是否经过符号链接或目录联接，阻止浏览器跟随链接逃离案例目录。</summary>
public interface IReportPathSecurity
{
    bool IsReparsePoint(string path);
}

/// <summary>使用 Windows 文件属性识别符号链接、目录联接等重解析点。</summary>
public sealed class WindowsReportPathSecurity : IReportPathSecurity
{
    public bool IsReparsePoint(string path)
        => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}

/// <summary>
/// 报告安全打开服务。报告入口固定为 Case 解压目录下 Report/index.html，
/// 只有浏览器成功启动后才记录最后打开时间，避免失败操作污染审计数据。
/// </summary>
public sealed class ReportOpenService : IReportOpenService
{
    private readonly Func<string, CancellationToken, Task<HephaestusWorkbench.Core.Models.AnalysisCase?>> _getCase;
    private readonly IReportRepository _reports;
    private readonly IReportProcessLauncher _processLauncher;
    private readonly WorkbenchLogger _logger;
    private readonly IReportPathSecurity _pathSecurity;
    private readonly TimeProvider _timeProvider;

    public ReportOpenService(
        IAnalysisCaseRepository cases,
        IReportRepository reports,
        IReportProcessLauncher processLauncher,
        WorkbenchLogger logger,
        TimeProvider? timeProvider = null)
        : this(cases, reports, processLauncher, logger, new WindowsReportPathSecurity(), timeProvider)
    {
    }

    public ReportOpenService(
        IAnalysisCaseRepository cases,
        IReportRepository reports,
        IReportProcessLauncher processLauncher,
        WorkbenchLogger logger,
        IReportPathSecurity pathSecurity,
        TimeProvider? timeProvider = null)
        : this(cases.GetAsync, reports, processLauncher, logger, pathSecurity, timeProvider)
    {
    }

    public ReportOpenService(
        CaseAnalysisService analysis,
        IReportRepository reports,
        IReportProcessLauncher processLauncher,
        WorkbenchLogger logger,
        TimeProvider? timeProvider = null)
        : this(analysis.GetCaseAsync, reports, processLauncher, logger, new WindowsReportPathSecurity(), timeProvider)
    {
    }

    private ReportOpenService(
        Func<string, CancellationToken, Task<HephaestusWorkbench.Core.Models.AnalysisCase?>> getCase,
        IReportRepository reports,
        IReportProcessLauncher processLauncher,
        WorkbenchLogger logger,
        IReportPathSecurity pathSecurity,
        TimeProvider? timeProvider)
    {
        _getCase = getCase;
        _reports = reports;
        _processLauncher = processLauncher;
        _logger = logger;
        _pathSecurity = pathSecurity;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ReportOpenResult> OpenAsync(ReportOpenRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.ReportId))
            return Failure("报告标识不能为空。");

        try
        {
            var report = await _reports.GetAsync(request.ReportId, cancellationToken);
            if (report is null) return Failure("报告记录不存在，可能已被清理。");

            var analysisCase = await _getCase(report.CaseId, cancellationToken);
            if (analysisCase is null) return Failure("报告所属的分析记录不存在，无法确认报告路径安全性。");

            var extractPath = Path.GetFullPath(analysisCase.ExtractPath);
            var reportPath = Path.GetFullPath(report.Path);
            var expectedReportPath = Path.GetFullPath(Path.Combine(extractPath, "Report"));
            if (!string.Equals(reportPath, expectedReportPath, StringComparison.OrdinalIgnoreCase))
                return Failure("报告目录必须位于案例解压目录下的 Report 路径，已拒绝打开。");

            var entryPath = Path.GetFullPath(Path.Combine(reportPath, "index.html"));
            if (!IsChildPath(entryPath, reportPath))
                return Failure("报告入口路径越界，已拒绝打开。");
            if (!File.Exists(entryPath))
                return Failure("报告入口文件 index.html 不存在，请重新分析日志。", report.Id);
            if (_pathSecurity.IsReparsePoint(extractPath)
                || _pathSecurity.IsReparsePoint(reportPath)
                || _pathSecurity.IsReparsePoint(entryPath))
                return Failure("案例解压目录、报告目录或入口包含文件系统链接，已拒绝打开。", report.Id);

            try
            {
                _processLauncher.Open(entryPath);
            }
            catch (Exception ex)
            {
                return Failure($"无法使用 Windows 默认浏览器打开报告：{ex.Message}", report.Id, ex);
            }

            var openedAt = _timeProvider.GetLocalNow().DateTime;
            try
            {
                await _reports.UpdateLastOpenedAtAsync(report.Id, openedAt, cancellationToken);
            }
            catch (Exception ex)
            {
                const string warning = "报告已在默认浏览器中打开，但无法记录最后打开时间。";
                _logger.Error($"{warning} 报告 {report.Id}", ex);
                return new ReportOpenResult(true, entryPath, warning);
            }

            _logger.Info($"已使用默认浏览器打开报告：报告 {report.Id}，入口 {entryPath}");
            return new ReportOpenResult(true, entryPath, null, openedAt);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Failure($"报告路径无效或无法访问：{ex.Message}", request.ReportId, ex);
        }
    }

    private ReportOpenResult Failure(string message, string? reportId = null, Exception? exception = null)
    {
        var auditMessage = reportId is null ? message : $"打开报告失败：报告 {reportId}，{message}";
        _logger.Error(auditMessage, exception);
        return new ReportOpenResult(false, null, message);
    }

    private static bool IsChildPath(string path, string parent)
    {
        var relative = Path.GetRelativePath(parent, path);
        return relative is not "." and not ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }
}


