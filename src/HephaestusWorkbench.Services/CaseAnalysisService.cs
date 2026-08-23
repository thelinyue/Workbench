using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Repositories;
using HephaestusWorkbench.Data;
using HephaestusWorkbench.PluginSDK;
using AnalysisTaskStatus = HephaestusWorkbench.Core.Models.TaskStatus;
using ProtocolAnalysisScope = HephaestusWorkbench.PluginSDK.AnalysisScope;

namespace HephaestusWorkbench.Services;

/// <summary>
/// 串起日志、Case、唯一分析扩展和报告的核心业务服务。UI 只调用该服务，不直接操作文件或进程。
/// 每次真正执行前从 Registry 租用当前 healthy 版本，确保运行中的任务不会被扩展更新清理。
/// </summary>
public sealed class CaseAnalysisService
{
    private readonly DataPaths _paths;
    private readonly IAnalysisCaseRepository _cases;
    private readonly IAnalysisTaskRepository _tasks;
    private readonly IReportRepository _reports;
    private readonly ExtensionRegistry _extensions;
    private readonly AnalysisProcessHost _processHost;
    private readonly TaskCenter _taskCenter;
    private readonly WorkbenchLogger _logger;
    private readonly RuleSetService _rules;
    private readonly IAnalysisLifecycleRepository _lifecycle;
    public event EventHandler? StateChanged;

    public sealed record CleanupResult(int Deleted, int Skipped, int Failed);

    public CaseAnalysisService(
        DataPaths paths,
        IAnalysisCaseRepository cases,
        IAnalysisTaskRepository tasks,
        IReportRepository reports,
        ExtensionRegistry extensions,
        AnalysisProcessHost processHost,
        TaskCenter taskCenter,
        WorkbenchLogger logger,
        RuleSetService rules,
        IAnalysisLifecycleRepository lifecycle)
    {
        _paths = paths;
        _cases = cases;
        _tasks = tasks;
        _reports = reports;
        _extensions = extensions ?? throw new ArgumentNullException(nameof(extensions));
        _processHost = processHost ?? throw new ArgumentNullException(nameof(processHost));
        _taskCenter = taskCenter;
        _logger = logger;
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
    }

    public async Task<AnalysisTask?> StartAsync(LogInboxItem item, CancellationToken cancellationToken = default)
    {
        if (!item.IsValidArchive)
        {
            _logger.Error($"日志无法创建案例：{item.ErrorMessage ?? "压缩包无效"}");
            return null;
        }

        var engines = (await _extensions.LoadAsync(cancellationToken))
            .Where(IsAnalysisEngine)
            .ToArray();
        if (engines.Length == 0)
        {
            _logger.Error("没有可用的日志分析扩展。请先在扩展中心安装并启用官方日志分析扩展。");
            return null;
        }
        if (engines.Length > 1)
        {
            _logger.Error($"检测到多个日志分析引擎（{string.Join("、", engines.Select(x => x.Id))}），已拒绝自动选择。正式版只允许一个活动分析引擎。");
            return null;
        }
        var engine = engines[0];

        var caseId = Guid.NewGuid().ToString("N");
        var sourcePath = Path.GetFullPath(item.FilePath);
        var sourceDirectory = Path.GetDirectoryName(sourcePath) ?? throw new InvalidOperationException("原始日志路径没有父目录。");
        var extractDirectory = Path.Combine(sourceDirectory, FileUtilities.RemoveAllExtensions(Path.GetFileName(sourcePath)));
        var now = DateTime.Now;
        var analysisCase = new AnalysisCase
        {
            Id = caseId,
            DisplayName = FileUtilities.RemoveAllExtensions(item.FileName),
            OriginalName = item.FileName,
            DeviceId = item.DeviceId,
            LogTime = item.LogTime,
            Status = CaseStatus.Ready,
            SourcePath = sourcePath,
            ExtractPath = extractDirectory,
            ReportPath = null,
            CreateTime = now,
            UpdateTime = now
        };
        var task = new AnalysisTask
        {
            Id = Guid.NewGuid().ToString("N"),
            CaseId = caseId,
            PluginId = engine.Id,
            Status = AnalysisTaskStatus.Waiting
        };
        await _lifecycle.CreateAsync(analysisCase, task, cancellationToken);
        StateChanged?.Invoke(this, EventArgs.Empty);
        _ = _taskCenter.EnqueueAsync(task, token => RunAsync(analysisCase, task, token));
        return task;
    }

    /// <summary>只有严格 v2 的 analysis/process 引擎能进入自动分析链路，Workspace 页面不会被误执行。</summary>
    private static bool IsAnalysisEngine(ExtensionManifest extension)
        => extension.Kind == ExtensionKind.Analysis
           && extension.Runtime.Kind == ExtensionRuntimeKind.Process
           && string.Equals(extension.Runtime.Protocol, AnalysisProcessProtocol.Version, StringComparison.Ordinal)
           && extension.SupportsCapability("analysis.engine");

    /// <summary>
    /// 提交并等待一次分析完成，供需要立即展示新报告的重新分析操作使用。
    /// </summary>
    public async Task<AnalysisTask?> StartAndWaitAsync(LogInboxItem item, CancellationToken cancellationToken = default)
    {
        var task = await StartAsync(item, cancellationToken);
        if (task is not null) await _taskCenter.WaitForCompletionAsync(task.Id, cancellationToken);
        return task;
    }

    public async Task<bool> CancelAsync(string taskId)
    {
        var result = _taskCenter.Cancel(taskId);
        if (!result) return false;
        var task = await _tasks.GetAsync(taskId);
        if (task is { Status: AnalysisTaskStatus.Waiting })
        {
            task.Status = AnalysisTaskStatus.Cancelled;
            task.EndTime = DateTime.Now;
            task.ErrorMessage = "任务已取消。";
            await _tasks.UpdateAsync(task);
            var item = await _cases.GetAsync(task.CaseId);
            if (item is not null)
            {
                item.Status = CaseStatus.Failed;
                item.ErrorMessage = "任务已取消。";
                item.UpdateTime = DateTime.Now;
                await _cases.UpdateAsync(item);
            }
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        return true;
    }

    public async Task<IReadOnlyList<AnalysisCase>> ListCasesAsync(CancellationToken cancellationToken = default) => await _cases.ListAsync(cancellationToken);
    public Task<AnalysisCase?> GetCaseAsync(string caseId, CancellationToken cancellationToken = default) => _cases.GetAsync(caseId, cancellationToken);
    public async Task<IReadOnlyList<AnalysisTask>> ListTasksAsync(CancellationToken cancellationToken = default) => await _tasks.ListAsync(cancellationToken);
    public Task<AnalysisTask?> GetTaskAsync(string taskId, CancellationToken cancellationToken = default) => _tasks.GetAsync(taskId, cancellationToken);

    public async Task RenameAsync(string caseId, string displayName, CancellationToken cancellationToken = default)
    {
        var item = await _cases.GetAsync(caseId, cancellationToken) ?? throw new InvalidOperationException("案例不存在。");
        item.DisplayName = displayName.Trim();
        item.UpdateTime = DateTime.Now;
        await _cases.UpdateAsync(item, cancellationToken);
    }

    /// <summary>删除案例所属源日志的完整生命周期，确保文件和全部关联记录一起删除。</summary>
    public async Task DeleteAsync(string caseId, CancellationToken cancellationToken = default)
    {
        var item = await _cases.GetAsync(caseId, cancellationToken);
        if (item is null) return;
        await DeleteLifecycleAsync(item.SourcePath, cancellationToken);
    }

    /// <summary>
    /// 删除同一源日志形成的完整生命周期。所有关联案例路径会在真正删除前统一校验，
    /// 并且只要存在等待或运行中的任务就拒绝执行，防止插件仍在读写时破坏数据。
    /// </summary>
    public async Task DeleteLifecycleAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        var normalizedSourcePath = NormalizePath(sourcePath);
        var cases = (await _cases.ListAsync(cancellationToken))
            .Where(x => string.Equals(NormalizePath(x.SourcePath), normalizedSourcePath, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (cases.Length == 0) return;

        var caseIds = cases.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hasActiveTask = (await _tasks.ListAsync(cancellationToken)).Any(x => caseIds.Contains(x.CaseId)
            && x.Status is AnalysisTaskStatus.Waiting or AnalysisTaskStatus.Running);
        if (hasActiveTask)
            throw new InvalidOperationException("该日志仍有等待或运行中的分析任务，无法删除全部数据。");

        // 必须先完成整组校验，之后才允许产生任何文件系统副作用。
        foreach (var item in cases) FileUtilities.ValidateCaseArtifacts(item, _paths);

        try
        {
            foreach (var item in cases)
                FileUtilities.DeleteCaseArtifacts(item, _paths, deleteReport: true);

            // 文件删除成功后，再用单个数据库事务清理全部关联记录；不依赖旧库的外键级联。
            await _lifecycle.DeleteByCaseIdsAsync(caseIds, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Error($"删除日志完整生命周期失败：{normalizedSourcePath}", ex);
            throw new InvalidOperationException($"删除日志完整生命周期失败：{ex.Message}", ex);
        }

        _logger.Info($"删除完成：日志完整生命周期，共 {cases.Length} 个案例，{normalizedSourcePath}");
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 手动清理超过保留期限的完整分析生命周期。候选只包含已有报告且没有活动任务的日志，
    /// 每个源日志独立处理，单条失败不会阻断其他候选项。
    /// </summary>
    public async Task<CleanupResult> CleanupExpiredAsync(int retentionDays, CancellationToken cancellationToken = default)
    {
        if (retentionDays is < 1 or > 7)
            throw new ArgumentOutOfRangeException(nameof(retentionDays), "清理保留天数必须在 1 到 7 天之间。");

        var cutoff = DateTime.Now.AddDays(-retentionDays);
        var cases = await _cases.ListAsync(cancellationToken);
        var tasks = await _tasks.ListAsync(cancellationToken);
        var reports = await _reports.ListAsync(new ReportQuery(), cancellationToken);
        var activeCaseIds = tasks.Where(x => x.Status is AnalysisTaskStatus.Waiting or AnalysisTaskStatus.Running)
            .Select(x => x.CaseId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var caseById = cases.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var targets = reports
            .Where(x => x.CreateTime < cutoff && caseById.ContainsKey(x.CaseId) && !activeCaseIds.Contains(x.CaseId))
            .Select(x => caseById[x.CaseId].SourcePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var deleted = 0;
        var failed = 0;
        foreach (var sourcePath in targets)
        {
            try
            {
                await DeleteLifecycleAsync(sourcePath, cancellationToken);
                deleted++;
            }
            catch (Exception ex)
            {
                failed++;
                _logger.Error($"自动清理到期分析失败：{sourcePath}", ex);
            }
        }

        var skipped = reports.Count(x => x.CreateTime >= cutoff || !caseById.ContainsKey(x.CaseId) || activeCaseIds.Contains(x.CaseId));
        return new CleanupResult(deleted, skipped, failed);
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("源日志路径不能为空。", nameof(path));
        return Path.GetFullPath(path.Trim());
    }

    private async Task RunAsync(AnalysisCase analysisCase, AnalysisTask task, CancellationToken cancellationToken)
    {
        ExtensionVersionLease? lease = null;
        try
        {
            task.Status = AnalysisTaskStatus.Running;
            task.StartTime = DateTime.Now;
            analysisCase.Status = CaseStatus.Running;
            analysisCase.UpdateTime = DateTime.Now;
            await _lifecycle.MarkRunningAsync(analysisCase, task);
            StateChanged?.Invoke(this, EventArgs.Empty);

            // 租约从实际运行开始持续到最终状态落库完成。扩展中心可以切换 current，
            // 但正常完成和异常补偿落库都结束前，不能清理这里固定的版本目录。
            lease = _extensions.LeaseCurrentVersion(task.PluginId);
            var request = new AnalysisProcessRequest
            {
                Protocol = AnalysisProcessProtocol.Version,
                RequestId = task.Id,
                CaseId = analysisCase.Id,
                SourcePath = analysisCase.SourcePath,
                OutputDirectory = FileUtilities.GetReportDirectory(analysisCase.ExtractPath),
                ExtractDirectory = analysisCase.ExtractPath,
                RulesPath = _rules.HasActiveRules ? _rules.ActiveRulesPath : null,
                Scope = task.AnalysisScope switch
                {
                    HephaestusWorkbench.Core.Models.AnalysisScope.Comprehensive => ProtocolAnalysisScope.Comprehensive,
                    HephaestusWorkbench.Core.Models.AnalysisScope.Storage => ProtocolAnalysisScope.Storage,
                    _ => throw new InvalidOperationException($"不支持的分析范围：{task.AnalysisScope}")
                }
            };
            var result = await _processHost.RunAsync(lease.Manifest, request, cancellationToken);
            task.EndTime = DateTime.Now;
            task.ReportPath = result.ReportDirectory;
            task.ErrorMessage = result.ErrorMessage;

            analysisCase.Status = result.Succeeded ? CaseStatus.Completed : CaseStatus.Failed;
            analysisCase.ReportPath = result.ReportDirectory;
            analysisCase.ErrorMessage = result.ErrorMessage;
            analysisCase.UpdateTime = DateTime.Now;
            task.Status = result.Cancelled
                ? AnalysisTaskStatus.Cancelled
                : result.Succeeded ? AnalysisTaskStatus.Completed : AnalysisTaskStatus.Failed;

            var report = !result.Succeeded || result.ReportDirectory is null
                ? null
                : new Report
                {
                    Id = Guid.NewGuid().ToString("N"),
                    CaseId = analysisCase.Id,
                    Path = result.ReportDirectory,
                    PluginId = lease.Manifest.Id,
                    PluginName = lease.Manifest.Name,
                    PluginVersion = lease.Manifest.Version,
                    CreateTime = DateTime.Now
                };
            await _lifecycle.CompleteAsync(analysisCase, task, report);

            // 只有报告记录和案例状态都落库后，任务才对外呈现为完成，避免读取到半完成状态。
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            task.Status = AnalysisTaskStatus.Failed;
            task.EndTime = DateTime.Now;
            task.ErrorMessage = $"分析任务异常终止：{ex.Message}";
            analysisCase.Status = CaseStatus.Failed;
            analysisCase.ErrorMessage = task.ErrorMessage;
            analysisCase.UpdateTime = DateTime.Now;
            try
            {
                await _lifecycle.CompleteAsync(analysisCase, task, null);
            }
            catch (Exception persistException)
            {
                _logger.Error($"分析任务失败状态写回数据库失败：{task.Id}", persistException);
            }
            _logger.Error($"分析任务异常终止：{task.Id}", ex);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            lease?.Dispose();
        }
    }}
