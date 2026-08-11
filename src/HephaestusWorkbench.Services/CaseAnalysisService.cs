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
        WorkbenchConfigurationService? configuration = null)
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
            Path.GetDirectoryName(analysisCase.SourcePath) ?? _paths.Root);
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
