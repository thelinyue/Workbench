using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Repositories;
using HephaestusWorkbench.Data;
using HephaestusWorkbench.PluginSDK;
using AnalysisTaskStatus = HephaestusWorkbench.Core.Models.TaskStatus;

namespace HephaestusWorkbench.Services;

/// <summary>
/// 串起日志、Case、插件和报告的核心业务服务。UI 只调用该服务，不直接操作文件或进程。
/// </summary>
public sealed class CaseAnalysisService
{
    private readonly DataPaths _paths;
    private readonly IAnalysisCaseRepository _cases;
    private readonly IAnalysisTaskRepository _tasks;
    private readonly IReportRepository _reports;
    private readonly IPluginCatalog _catalog;
    private readonly IPluginRunner _legacyRunner;
    private readonly IPluginRunner _standardRunner;
    private readonly TaskCenter _taskCenter;
    private readonly WorkbenchLogger _logger;
    private readonly WorkbenchConfigurationService? _configuration;
    private readonly RuleSetService? _rules;
    public event EventHandler? StateChanged;

    public CaseAnalysisService(
        DataPaths paths,
        IAnalysisCaseRepository cases,
        IAnalysisTaskRepository tasks,
        IReportRepository reports,
        IPluginCatalog catalog,
        IPluginRunner legacyRunner,
        IPluginRunner standardRunner,
        TaskCenter taskCenter,
        WorkbenchLogger logger,
        WorkbenchConfigurationService? configuration = null,
        RuleSetService? rules = null)
    {
        _paths = paths;
        _cases = cases;
        _tasks = tasks;
        _reports = reports;
        _catalog = catalog;
        _legacyRunner = legacyRunner;
        _standardRunner = standardRunner;
        _taskCenter = taskCenter;
        _logger = logger;
        _configuration = configuration;
        _rules = rules;
    }

    public async Task<AnalysisTask?> StartAsync(LogInboxItem item, CancellationToken cancellationToken = default)
    {
        if (!item.IsValidArchive)
        {
            _logger.Error($"日志无法创建案例：{item.ErrorMessage ?? "压缩包无效"}");
            return null;
        }

        var plugins = await _catalog.ScanAsync(cancellationToken);
        PluginManifest? plugin;
        if (_configuration is null)
        {
            plugin = plugins.FirstOrDefault(x => x.Runner == "legacy-log-analyzer") ?? plugins.FirstOrDefault();
        }
        else
        {
            var config = await _configuration.EnsurePluginConfigAsync(cancellationToken);
            var enabledIds = config.Plugins.Where(x => x.Enabled).Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            plugin = plugins.FirstOrDefault(x => enabledIds.Contains(x.Id)
                && string.Equals(x.Id, config.DefaultPluginId, StringComparison.OrdinalIgnoreCase));
        }
        if (plugin is null)
        {
            _logger.Error("没有可用的日志分析插件。");
            return null;
        }

        var caseId = Guid.NewGuid().ToString("N");
        var sourcePath = Path.GetFullPath(item.FilePath);
        var sourceDirectory = Path.GetDirectoryName(sourcePath) ?? throw new InvalidOperationException("原始日志路径没有父目录。");
        var extractDirectory = Path.Combine(sourceDirectory, FileUtilities.RemoveAllExtensions(Path.GetFileName(sourcePath)));
        var reportDirectory = _paths.GetCaseReportDirectory(caseId);

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
        await _cases.InsertAsync(analysisCase, cancellationToken);

        var task = new AnalysisTask
        {
            Id = Guid.NewGuid().ToString("N"),
            CaseId = caseId,
            PluginId = plugin.Id,
            Status = AnalysisTaskStatus.Waiting
        };
        await _tasks.InsertAsync(task, cancellationToken);
        StateChanged?.Invoke(this, EventArgs.Empty);
        _ = _taskCenter.EnqueueAsync(task, token => RunAsync(analysisCase, task, plugin, token));
        return task;
    }

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

    public async Task DeleteAsync(string caseId, CancellationToken cancellationToken = default)
    {
        var item = await _cases.GetAsync(caseId, cancellationToken);
        if (item is null) return;
        try
        {
            FileUtilities.DeleteCaseArtifacts(item, _paths, deleteReport: true);
        }
        catch (Exception ex)
        {
            _logger.Error($"删除案例数据失败：{item.DisplayName}", ex);
            throw new InvalidOperationException($"删除案例数据失败：{ex.Message}", ex);
        }
        await _cases.DeleteAsync(caseId, cancellationToken);
        StateChanged?.Invoke(this, EventArgs.Empty);
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
            {
                FileUtilities.DeleteCaseArtifacts(item, _paths, deleteReport: true);
                await _cases.DeleteAsync(item.Id, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"删除日志完整生命周期失败：{normalizedSourcePath}", ex);
            throw new InvalidOperationException($"删除日志完整生命周期失败：{ex.Message}", ex);
        }

        _logger.Info($"删除完成：日志完整生命周期，共 {cases.Length} 个案例，{normalizedSourcePath}");
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("源日志路径不能为空。", nameof(path));
        return Path.GetFullPath(path.Trim());
    }

    private async Task RunAsync(AnalysisCase analysisCase, AnalysisTask task, PluginManifest plugin, CancellationToken cancellationToken)
    {
        task.Status = AnalysisTaskStatus.Running;
        task.StartTime = DateTime.Now;
        await _tasks.UpdateAsync(task);
        analysisCase.Status = CaseStatus.Running;
        analysisCase.UpdateTime = DateTime.Now;
        await _cases.UpdateAsync(analysisCase);
        StateChanged?.Invoke(this, EventArgs.Empty);

        var context = new PluginExecutionContext(
            analysisCase.Id,
            analysisCase.SourcePath,
            _paths.GetCaseReportDirectory(analysisCase.Id),
            analysisCase.ExtractPath,
            Path.GetDirectoryName(analysisCase.SourcePath) ?? _paths.Root,
            _rules?.HasActiveRules == true ? _rules.ActiveRulesPath : null);
        var runner = plugin.Runner == "legacy-log-analyzer" ? _legacyRunner : _standardRunner;
        var result = await runner.RunAsync(plugin, context, cancellationToken);
        task.EndTime = DateTime.Now;
        task.ReportPath = result.ReportPath;
        task.ErrorMessage = result.ErrorMessage;
        task.Status = result.Cancelled ? AnalysisTaskStatus.Cancelled : result.ReportPath is null ? AnalysisTaskStatus.Failed : AnalysisTaskStatus.Completed;
        await _tasks.UpdateAsync(task);

        analysisCase.Status = result.ReportPath is null ? CaseStatus.Failed : CaseStatus.Completed;
        analysisCase.ReportPath = result.ReportPath;
        analysisCase.ErrorMessage = result.ErrorMessage;
        analysisCase.UpdateTime = DateTime.Now;
        await _cases.UpdateAsync(analysisCase);
        if (result.ReportPath is not null)
        {
            await _reports.InsertAsync(new Report
            {
                Id = Guid.NewGuid().ToString("N"),
                CaseId = analysisCase.Id,
                Path = result.ReportPath,
                PluginId = plugin.Id,
                CreateTime = DateTime.Now
            });
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
