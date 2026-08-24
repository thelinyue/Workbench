using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Services;
using HephaestusWorkbench.Services;
using Wpf = System.Windows;
using AnalysisTaskStatus = HephaestusWorkbench.Core.Models.TaskStatus;

namespace HephaestusWorkbench.App.ViewModels;

/// <summary>
/// 分析中心把收件箱文件、案例、后台任务和报告按源日志路径聚合成一个生命周期。
/// 聚合只发生在界面层，不改变现有数据库结构；刷新时始终重新读取业务服务，避免缓存状态与后台任务脱节。
/// </summary>
public sealed class AnalysisCenterViewModel : ViewModelBase, IDisposable
{
    private readonly LogInboxService _inbox;
    private readonly CaseAnalysisService _analysis;
    private readonly ReportService _reportService;
    private readonly IReportOpenService _reportOpenService;
    private readonly Action<string> _openExtractDirectory;
    private readonly WorkbenchLogger _logger;
    private readonly Func<string, bool> _confirmDeleteLifecycle;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly object _loadLifecycleSync = new();
    private readonly List<AnalysisLogGroupViewModel> _allItems = new();
    private readonly HashSet<string> _submittingSources = new(StringComparer.OrdinalIgnoreCase);
    private AnalysisLogGroupViewModel? _selectedItem;
    private AnalysisAttemptViewModel? _selectedAttempt;
    private string _message = string.Empty;
    private string? _operationMessage;
    private bool _isBulkOperationActive;
    private int _suppressStateRefresh;
    private int _pendingLoadOperations;
    private bool _disposed;

    public AnalysisCenterViewModel(
        LogInboxService inbox,
        CaseAnalysisService analysis,
        ReportService reportService,
        Action<string> openExtractDirectory,
        WorkbenchLogger logger,
        Func<string, bool>? confirmDeleteLifecycle = null,
        IReportOpenService? reportOpenService = null)
    {
        _inbox = inbox;
        _analysis = analysis;
        _reportService = reportService;
        _reportOpenService = reportOpenService ?? reportService.CreateOpenService(logger);
        _openExtractDirectory = openExtractDirectory;
        _logger = logger;
        _confirmDeleteLifecycle = confirmDeleteLifecycle ?? (message => Wpf.MessageBox.Show(message, "删除全部数据", Wpf.MessageBoxButton.YesNo, Wpf.MessageBoxImage.Warning) == Wpf.MessageBoxResult.Yes);

        RefreshCommand = new DelegateCommand(() => { _operationMessage = null; _ = LoadAsync(); }, () => !IsBulkOperationActive);
        OpenRowReportCommand = new DelegateCommand(parameter => _ = OpenRowReportAsync(parameter), parameter => ResolveReport(parameter)?.IsAvailable == true);
        AnalyzeAllPendingCommand = new DelegateCommand(() => _ = AnalyzeAllPendingAsync(), () => !IsBulkOperationActive && BulkEligibleCount > 0);
        DeleteInvalidCommand = new DelegateCommand(() => _ = DeleteInvalidAsync(), () => !IsBulkOperationActive && InvalidDeleteCount > 0);
        AnalyzeSingleCommand = new DelegateCommand(parameter => _ = AnalyzeSingleAsync(parameter as AnalysisLogGroupViewModel), CanAnalyzeSingle);
        CancelAnalysisCommand = new DelegateCommand(parameter => _ = CancelAnalysisAsync(parameter as AnalysisLogGroupViewModel), parameter => parameter is AnalysisLogGroupViewModel { CanCancelAnalysis: true });
        OpenExtractDirectoryCommand = new DelegateCommand(OpenExtractDirectory, CanOpenExtractDirectory);
        OpenReportFolderCommand = new DelegateCommand(OpenReportFolder, parameter => ResolveAttempt(parameter)?.Report is not null);
        DeleteLifecycleCommand = new DelegateCommand(parameter => _ = DeleteLifecycleAsync(parameter as AnalysisLogGroupViewModel), parameter => parameter is AnalysisLogGroupViewModel item && item.CanDeleteLifecycle && !IsBulkOperationActive);

        _inbox.ItemsChanged += OnSourceStateChanged;
        _inbox.ConfigurationChanged += OnSourceStateChanged;
        _analysis.StateChanged += OnSourceStateChanged;
    }

    public ObservableCollection<AnalysisLogGroupViewModel> Items { get; } = new();
    public ICommand RefreshCommand { get; }
    public ICommand OpenRowReportCommand { get; }
    public ICommand AnalyzeAllPendingCommand { get; }
    public ICommand DeleteInvalidCommand { get; }
    public ICommand AnalyzeSingleCommand { get; }
    public ICommand CancelAnalysisCommand { get; }
    public ICommand OpenExtractDirectoryCommand { get; }
    public ICommand OpenReportFolderCommand { get; }
    public ICommand DeleteLifecycleCommand { get; }

    public bool ShowEmptyState => Items.Count == 0;
    public IEnumerable<AnalysisLogGroupViewModel> PendingItems => Items.Where(x => x.StageKey is "pending" or "active" or "invalid");
    /// <summary>
    /// 历史记录按“分析尝试”展开，而不是按源日志聚合；同一日志重试三次必须保留三条可独立打开的记录。
    /// 待分析列表仍继续使用 <see cref="Items"/> 的源日志聚合行，避免两个列表混用同一种数据粒度。
    /// </summary>
    public IEnumerable<AnalysisHistoryItemViewModel> HistoryItems => Items
        .SelectMany(group => group.Attempts.Select(attempt => new AnalysisHistoryItemViewModel(group, attempt)))
        .OrderByDescending(item => item.ActivityTime);
    public bool HasSelection => SelectedItem is not null;
    public int BulkEligibleCount => Items.Count(x => x.IsBulkEligible);
    public int InvalidDeleteCount => Items.Count(x => x.IsInvalidDeleteEligible);
    public bool IsBulkOperationActive
    {
        get => _isBulkOperationActive;
        private set
        {
            if (!SetProperty(ref _isBulkOperationActive, value)) return;
            RaiseCommands();
        }
    }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public AnalysisLogGroupViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!SetProperty(ref _selectedItem, value)) return;
            SelectedAttempt = value?.CurrentAttempt;
            OnPropertyChanged(nameof(HasSelection));
            RaiseCommands();
        }
    }

    public AnalysisAttemptViewModel? SelectedAttempt
    {
        get => _selectedAttempt;
        set
        {
            if (!SetProperty(ref _selectedAttempt, value)) return;
            RaiseCommands();
        }
    }

    public async Task InitializeAsync()
    {
        await LoadAsync();
    }

    public async Task LoadAsync()
    {
        lock (_loadLifecycleSync)
        {
            if (_disposed) return;
            _pendingLoadOperations++;
        }
        await _loadLock.WaitAsync();
        try
        {
            var selectedPath = SelectedItem?.SourcePath;
            var selectedCaseId = SelectedAttempt?.Case.Id;
            var casesTask = _analysis.ListCasesAsync();
            var tasksTask = _analysis.ListTasksAsync();
            var reportsTask = _reportService.ListAsync(new ReportQuery());
            await Task.WhenAll(casesTask, tasksTask, reportsTask);

            _allItems.Clear();
            _allItems.AddRange(BuildGroups(_inbox.Items, casesTask.Result, tasksTask.Result, reportsTask.Result));
            ApplyItems(selectedPath, selectedCaseId);
            Message = _operationMessage ?? string.Empty;
        }
        catch (Exception ex)
        {
            Message = $"加载分析中心失败：{ex.Message}";
            _logger.Error("加载分析中心失败", ex);
        }
        finally
        {
            _loadLock.Release();
            Interlocked.Decrement(ref _pendingLoadOperations);
        }
    }

    public async Task SelectCaseAsync(string caseId)
    {
        await LoadAsync();
        var group = _allItems.FirstOrDefault(x => x.Attempts.Any(a => string.Equals(a.Case.Id, caseId, StringComparison.OrdinalIgnoreCase)));
        if (group is null) return;
        SelectedItem = group;
        SelectedAttempt = group.Attempts.First(x => string.Equals(x.Case.Id, caseId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task SelectSourceAsync(string sourcePath)
    {
        await LoadAsync();
        SelectedItem = _allItems.FirstOrDefault(x => PathsEqual(x.SourcePath, sourcePath));
    }

    public async Task<bool> OpenCaseReportAsync(string caseId)
    {
        var report = await _reportService.GetLatestForCaseAsync(caseId);
        if (report is null) return false;
        var result = await _reportOpenService.OpenAsync(new ReportOpenRequest(report.Id));
        Message = GetReportOpenMessage(result);
        return result.Success;
    }

    private static IReadOnlyList<AnalysisLogGroupViewModel> BuildGroups(
        IReadOnlyList<LogInboxItem> inboxItems,
        IReadOnlyList<AnalysisCase> cases,
        IReadOnlyList<AnalysisTask> tasks,
        IReadOnlyList<ReportSummary> reports)
    {
        var builders = new Dictionary<string, AnalysisLogGroupBuilder>(StringComparer.OrdinalIgnoreCase);
        foreach (var inboxItem in inboxItems)
        {
            var key = NormalizePath(inboxItem.FilePath);
            if (!builders.TryGetValue(key, out var builder)) builders[key] = builder = new AnalysisLogGroupBuilder(key);
            builder.InboxItem = inboxItem;
        }

        var tasksByCase = tasks.GroupBy(x => x.CaseId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(t => t.StartTime ?? DateTime.MinValue).First(), StringComparer.OrdinalIgnoreCase);
        var reportsByCase = reports.GroupBy(x => x.CaseId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(r => r.CreateTime).First(), StringComparer.OrdinalIgnoreCase);

        foreach (var analysisCase in cases)
        {
            var key = NormalizePath(analysisCase.SourcePath);
            if (!builders.TryGetValue(key, out var builder)) builders[key] = builder = new AnalysisLogGroupBuilder(key);
            tasksByCase.TryGetValue(analysisCase.Id, out var task);
            reportsByCase.TryGetValue(analysisCase.Id, out var report);
            builder.Attempts.Add(new AnalysisAttemptViewModel(analysisCase, task, report));
        }

        return builders.Values
            .Select(x => x.Build())
            .OrderByDescending(x => x.LastActivityTime)
            .ToArray();
    }

    /// <summary>将聚合后的日志完整呈现给工程师，批量操作因此始终作用于全量列表。</summary>
    private void ApplyItems(string? selectedPath = null, string? selectedCaseId = null)
    {
        selectedPath ??= SelectedItem?.SourcePath;
        selectedCaseId ??= SelectedAttempt?.Case.Id;

        Items.Clear();
        foreach (var item in _allItems) Items.Add(item);
        var restored = selectedPath is null ? null : Items.FirstOrDefault(x => PathsEqual(x.SourcePath, selectedPath));
        SelectedItem = restored ?? Items.FirstOrDefault();
        if (selectedCaseId is not null && SelectedItem is not null)
            SelectedAttempt = SelectedItem.Attempts.FirstOrDefault(x => string.Equals(x.Case.Id, selectedCaseId, StringComparison.OrdinalIgnoreCase)) ?? SelectedItem.CurrentAttempt;
        Message = _operationMessage ?? string.Empty;
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(BulkEligibleCount));
        OnPropertyChanged(nameof(InvalidDeleteCount));
        OnPropertyChanged(nameof(PendingItems));
        OnPropertyChanged(nameof(HistoryItems));
        RaiseCommands();
    }

    private async Task OpenRowReportAsync(object? item)
    {
        if (ResolveReport(item) is not { IsAvailable: true } report)
        {
            Message = "该分析记录暂无可用报告。";
            return;
        }

        var result = await _reportOpenService.OpenAsync(new ReportOpenRequest(report.Id));
        Message = GetReportOpenMessage(result);
    }

    /// <summary>
    /// 报告已由浏览器成功打开时，持久化最后打开时间失败属于需要告知用户的警告，
    /// 不能再用通用成功文案覆盖服务返回的中文说明。
    /// </summary>
    private static string GetReportOpenMessage(ReportOpenResult result)
        => result.Success
            ? string.IsNullOrWhiteSpace(result.ErrorMessage) ? "报告已在默认浏览器中打开。" : result.ErrorMessage
            : result.ErrorMessage ?? "报告打开失败。";


    /// <summary>
    /// 快速分析单个用户选择的日志。该入口会等待分析完成，并且只为这一份日志自动打开报告；
    /// 监控目录和批量提交继续在后台运行，不会批量弹出浏览器窗口。
    /// </summary>
    public async Task AnalyzeFileAsync(string sourcePath)
    {
        if (IsBulkOperationActive || string.IsNullOrWhiteSpace(sourcePath)) return;
        var normalizedPath = NormalizePath(sourcePath);
        var item = _allItems.FirstOrDefault(x => PathsEqual(x.SourcePath, normalizedPath));
        if (item is null)
        {
            var inspection = await _inbox.InspectFileAsync(normalizedPath);
            if (!inspection.IsValid || inspection.Item is null)
            {
                Message = inspection.Item?.ErrorMessage ?? "所选文件不是可分析的诊断日志。";
                return;
            }
            item = new AnalysisLogGroupViewModel(normalizedPath, inspection.Item, Array.Empty<AnalysisAttemptViewModel>());
        }
        await AnalyzeSingleAsync(item, autoOpenReport: true);
    }
    private bool CanAnalyzeSingle(object? parameter)
        => parameter is AnalysisLogGroupViewModel item
            && !IsBulkOperationActive
            && item.CanAnalyzeSingle
            && !_submittingSources.Contains(item.SourcePath);

    private async Task AnalyzeSingleAsync(AnalysisLogGroupViewModel? item, bool autoOpenReport = false)
    {
        if (item is null || !CanAnalyzeSingle(item) || !_submittingSources.Add(item.SourcePath)) return;
        BeginBulkOperation();
        var feedback = string.Empty;
        try
        {
            var submission = await SubmitAnalysisCoreAsync(item, waitForCompletion: true);
            feedback = submission.Result switch
            {
                AnalysisSubmissionResult.Submitted => $"已提交分析任务：{item.FileName}",
                AnalysisSubmissionResult.Skipped => $"日志状态已变化，未提交：{item.FileName}",
                _ => $"提交分析任务失败：{item.FileName}"
            };
            if (autoOpenReport
                && submission.Result == AnalysisSubmissionResult.Submitted
                && submission.Task is { CaseId: var caseId, ReportPath: not null })
            {
                await LoadAsync();
                var report = await _reportService.GetLatestForCaseAsync(caseId);
                if (report is not null)
                {
                    var openResult = await _reportOpenService.OpenAsync(new ReportOpenRequest(report.Id));
                    feedback = openResult.Success
                        ? string.IsNullOrWhiteSpace(openResult.ErrorMessage)
                            ? $"分析完成，报告已在默认浏览器中打开：{item.FileName}"
                            : $"分析完成：{openResult.ErrorMessage}"
                        : $"分析完成，但报告打开失败：{openResult.ErrorMessage}";
                }
            }
        }
        catch (Exception ex)
        {
            feedback = $"提交分析任务失败：{ex.Message}";
            _logger.Error($"提交单份日志分析失败：{item.SourcePath}", ex);
        }
        finally
        {
            _submittingSources.Remove(item.SourcePath);
            EndBulkOperation();
            await LoadAsync();
            Message = _operationMessage = feedback;
        }
    }

    /// <summary>
    /// 批量按钮只提交当前筛选结果中的“待分析”日志。先复制快照再逐项创建任务，
    /// 避免任务状态事件刷新列表后改变本次批量操作的边界。
    /// </summary>

    /// <summary>取消当前日志记录上正在等待或运行的任务，不影响其他分析记录。</summary>
    private async Task CancelAnalysisAsync(AnalysisLogGroupViewModel? item)
    {
        var task = item?.ActiveTask;
        if (task is null) return;
        if (!await _analysis.CancelAsync(task.Id))
        {
            Message = "当前分析任务已结束，无法取消。";
            return;
        }
        Message = $"已请求取消分析：{item!.FileName}";
        await LoadAsync();
    }
    private async Task AnalyzeAllPendingAsync()
    {
        if (IsBulkOperationActive) return;
        var targets = Items.Where(x => x.IsBulkEligible).ToArray();
        if (targets.Length == 0) return;

        BeginBulkOperation();
        var submitted = 0;
        var skipped = 0;
        var failed = 0;
        try
        {
            foreach (var item in targets)
            {
                try
                {
                    switch (await SubmitAnalysisAsync(item))
                    {
                        case AnalysisSubmissionResult.Submitted: submitted++; break;
                        case AnalysisSubmissionResult.Skipped: skipped++; break;
                        default: failed++; break;
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.Error($"批量提交日志分析失败：{item.SourcePath}", ex);
                }
            }
        }
        finally
        {
            EndBulkOperation();
            await LoadAsync();
            Message = _operationMessage = $"批量分析提交完成：成功 {submitted} 个，跳过 {skipped} 个，失败 {failed} 个。后台最多同时分析 2 个任务。";
        }
    }

    private async Task<AnalysisSubmissionResult> SubmitAnalysisAsync(AnalysisLogGroupViewModel item)
    {
        return (await SubmitAnalysisCoreAsync(item, waitForCompletion: false)).Result;
    }

    /// <summary>
    /// 统一处理单条和批量分析的提交，单条重新分析可选择等待后台任务完成。
    /// </summary>
    private async Task<(AnalysisSubmissionResult Result, AnalysisTask? Task)> SubmitAnalysisCoreAsync(
        AnalysisLogGroupViewModel item,
        bool waitForCompletion)
    {
        if (!File.Exists(item.SourcePath) || item.HasActiveTask) return (AnalysisSubmissionResult.Skipped, null);
        var inspection = await _inbox.InspectFileAsync(item.SourcePath);
        if (!inspection.IsValid || inspection.Item is null) return (AnalysisSubmissionResult.Skipped, null);

        var task = waitForCompletion
            ? await _analysis.StartAndWaitAsync(inspection.Item)
            : await _analysis.StartAsync(inspection.Item);
        if (task is null) return (AnalysisSubmissionResult.Failed, null);
        var result = waitForCompletion && task.Status is not AnalysisTaskStatus.Completed
            ? AnalysisSubmissionResult.Failed
            : AnalysisSubmissionResult.Submitted;
        return (result, task);
    }

    private static AnalysisAttemptViewModel? ResolveAttempt(object? parameter)
        => parameter switch
        {
            AnalysisAttemptViewModel attempt => attempt,
            AnalysisHistoryItemViewModel history => history.Attempt,
            AnalysisLogGroupViewModel group => group.CurrentAttempt,
            _ => null
        };

    private static ReportSummary? ResolveReport(object? parameter)
        => parameter switch
        {
            AnalysisHistoryItemViewModel history => history.Attempt.Report,
            AnalysisAttemptViewModel attempt => attempt.Report,
            AnalysisLogGroupViewModel group => group.LatestAvailableReport,
            _ => null
        };

    private bool CanOpenExtractDirectory(object? parameter) => ResolveAttempt(parameter) is not null;

    private void OpenExtractDirectory(object? parameter)
    {
        var attempt = ResolveAttempt(parameter);
        if (attempt is not null) _openExtractDirectory(attempt.Case.ExtractPath);
    }

    private void OpenReportFolder(object? parameter)
    {
        var report = ResolveAttempt(parameter)?.Report;
        if (report is null) return;
        try
        {
            if (!Directory.Exists(report.Path))
            {
                Wpf.MessageBox.Show("报告目录不存在。", "无法打开位置", Wpf.MessageBoxButton.OK, Wpf.MessageBoxImage.Information);
                return;
            }
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{report.ReportFile}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.Error("打开报告位置失败", ex);
            Wpf.MessageBox.Show($"打开报告位置失败：{ex.Message}", "无法打开位置", Wpf.MessageBoxButton.OK, Wpf.MessageBoxImage.Error);
        }
    }

    private async Task DeleteLifecycleAsync(AnalysisLogGroupViewModel? item)
    {
        if (item is null || item.HasActiveTask) return;
        var reportCount = item.Attempts.Count(x => x.Report is not null);
        var extractPath = item.Attempts.FirstOrDefault()?.Case.ExtractPath ?? "无";
        var message = $"确认删除这份日志的全部数据吗？\n\n原始日志：{item.SourcePath}\n解压目录：{extractPath}\n案例：{item.Attempts.Count} 个\n报告：{reportCount} 个\n\n此操作不可恢复。";
        if (!_confirmDeleteLifecycle(message)) return;
        try
        {
            await DeleteLifecycleCoreAsync(item);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Message = ex.Message;
            Wpf.MessageBox.Show(ex.Message, "删除失败", Wpf.MessageBoxButton.OK, Wpf.MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 删除当前筛选中的无效日志。无效日志通常没有案例，此时生命周期服务没有可删除的数据库记录，
    /// 必须改由收件箱服务删除源文件；若将来存在残留案例，则仍走完整生命周期安全校验。
    /// </summary>
    private async Task DeleteInvalidAsync()
    {
        if (IsBulkOperationActive) return;
        var targets = Items.Where(x => x.IsInvalidDeleteEligible).ToArray();
        if (targets.Length == 0) return;
        var caseCount = targets.Sum(x => x.Attempts.Count);
        var reportCount = targets.Sum(x => x.Attempts.Count(a => a.Report is not null));
        var message = $"确认删除当前筛选结果中的 {targets.Length} 个无效日志吗？\n\n"
            + $"关联案例：{caseCount} 个\n关联报告：{reportCount} 个\n\n"
            + "报告文件、原始日志、解压目录及全部数据库记录（案例、任务、报告）都将被删除，此操作不可恢复。";
        if (!_confirmDeleteLifecycle(message)) return;

        BeginBulkOperation();
        var deleted = 0;
        var skipped = 0;
        var failed = 0;
        try
        {
            foreach (var item in targets)
            {
                try
                {
                    if (!File.Exists(item.SourcePath) && item.Attempts.Count == 0)
                    {
                        skipped++;
                        continue;
                    }
                    await DeleteLifecycleCoreAsync(item);
                    deleted++;
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.Error($"批量删除无效日志失败：{item.SourcePath}", ex);
                }
            }
        }
        finally
        {
            EndBulkOperation();
            await LoadAsync();
            Message = _operationMessage = $"异常日志清理完成：成功 {deleted} 个，跳过 {skipped} 个，失败 {failed} 个。";
        }
    }

    private async Task DeleteLifecycleCoreAsync(AnalysisLogGroupViewModel item)
    {
        if (item.Attempts.Count > 0)
        {
            await _analysis.DeleteLifecycleAsync(item.SourcePath);
            await _inbox.RefreshAsync();
            return;
        }

        var source = item.InboxItem ?? new LogInboxItem
        {
            FilePath = item.SourcePath,
            FileName = item.FileName,
            DeviceId = item.DeviceId,
            LogTime = item.LogTime,
            IsValidArchive = false,
            ErrorMessage = item.InboxError
        };
        await _inbox.DeleteAsync(source);
    }

    private void BeginBulkOperation()
    {
        Interlocked.Increment(ref _suppressStateRefresh);
        IsBulkOperationActive = true;
    }

    private void EndBulkOperation()
    {
        IsBulkOperationActive = false;
        Interlocked.Decrement(ref _suppressStateRefresh);
    }

    private void OnSourceStateChanged(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _suppressStateRefresh) > 0) return;
        RunOnUi(() => _ = LoadAsync());
    }

    private void RaiseCommands()
    {
        foreach (var command in new[]
        {
            RefreshCommand, OpenRowReportCommand, AnalyzeAllPendingCommand,
            DeleteInvalidCommand, AnalyzeSingleCommand, CancelAnalysisCommand, OpenExtractDirectoryCommand, OpenReportFolderCommand,
            DeleteLifecycleCommand
        })
            ((DelegateCommand)command).RaiseCanExecuteChanged();
    }

    private static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path.Trim(); }
    }

    private static bool PathsEqual(string first, string second)
        => string.Equals(NormalizePath(first), NormalizePath(second), StringComparison.OrdinalIgnoreCase);

    private static void RunOnUi(Action action)
    {
        var dispatcher = Wpf.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else _ = dispatcher.InvokeAsync(action);
    }

    public void Dispose()
    {
        lock (_loadLifecycleSync) _disposed = true;
        _inbox.ItemsChanged -= OnSourceStateChanged;
        _inbox.ConfigurationChanged -= OnSourceStateChanged;
        _analysis.StateChanged -= OnSourceStateChanged;
        // 等待正在进行的刷新释放数据库连接，避免应用退出或测试清理数据目录时发生文件占用。
        while (Volatile.Read(ref _pendingLoadOperations) > 0)
            Thread.Sleep(10);
        _loadLock.Dispose();
    }
}

/// <summary>单次案例、任务和报告的只读组合，供日志聚合和报告生命周期处理使用。</summary>
public sealed class AnalysisAttemptViewModel : ViewModelBase
{
    public AnalysisAttemptViewModel(AnalysisCase analysisCase, AnalysisTask? task, ReportSummary? report)
    {
        Case = analysisCase;
        Task = task;
        Report = report;
    }

    public AnalysisCase Case { get; }
    public AnalysisTask? Task { get; }
    public ReportSummary? Report { get; }
    public string PluginId => Report?.PluginId ?? Task?.PluginId ?? string.Empty;
    public string PluginName => Report?.PluginName ?? Task?.PluginId ?? "未知插件";
    public object StatusValue => (object?)Task?.Status ?? Case.Status;
    public string ErrorMessage => Task?.ErrorMessage ?? Case.ErrorMessage ?? string.Empty;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public DateTime ActivityTime => Task?.EndTime ?? Task?.StartTime ?? Case.UpdateTime;
    public bool IsActive => Task?.Status is AnalysisTaskStatus.Waiting or AnalysisTaskStatus.Running
        || Case.Status is CaseStatus.Ready or CaseStatus.Running;
}

/// <summary>
/// 历史列表的一行对应一次不可合并的分析尝试。该模型保留所属源日志用于展示，
/// 但报告、状态和时间始终来自当前尝试，避免“最新报告”覆盖较早尝试的真实结果。
/// </summary>
public sealed class AnalysisHistoryItemViewModel : ViewModelBase
{
    public AnalysisHistoryItemViewModel(AnalysisLogGroupViewModel group, AnalysisAttemptViewModel attempt)
    {
        Group = group;
        Attempt = attempt;
        StatusText = ResolveStatusText(attempt);
    }

    public AnalysisLogGroupViewModel Group { get; }
    public AnalysisAttemptViewModel Attempt { get; }
    public string FileName => Group.FileName;
    public string SourcePath => Group.SourcePath;
    public string DeviceId => Group.DeviceId;
    public DateTime ActivityTime => Attempt.ActivityTime;
    public string StatusText { get; }
    public object StatusValue => Attempt.StatusValue;
    public string ErrorMessage => Attempt.ErrorMessage;
    public bool HasError => Attempt.HasError;
    public bool CanOpenReport => Attempt.Report?.IsAvailable == true;

    private static string ResolveStatusText(AnalysisAttemptViewModel attempt)
    {
        if (attempt.IsActive)
            return attempt.Task?.Status == AnalysisTaskStatus.Running || attempt.Case.Status == CaseStatus.Running ? "分析中" : "等待分析";
        if (attempt.Case.Status == CaseStatus.Completed)
            return attempt.Report?.IsAvailable == true ? "已完成" : "报告不可用";
        if (attempt.Task?.Status == AnalysisTaskStatus.Cancelled)
            return "已取消";
        return "失败";
    }
}

/// <summary>同一源日志的聚合行；主列表状态优先采用活动分析，否则采用最近一次分析结果。</summary>
public sealed class AnalysisLogGroupViewModel : ViewModelBase
{
    public AnalysisLogGroupViewModel(string sourcePath, LogInboxItem? inboxItem, IReadOnlyList<AnalysisAttemptViewModel> attempts)
    {
        SourcePath = sourcePath;
        InboxItem = inboxItem;
        Attempts = new ObservableCollection<AnalysisAttemptViewModel>(attempts.OrderByDescending(x => x.ActivityTime));
        CurrentAttempt = Attempts.FirstOrDefault(x => x.IsActive) ?? Attempts.FirstOrDefault();
        LatestAvailableReport = Attempts.Select(x => x.Report).FirstOrDefault(x => x?.IsAvailable == true);
        var latestCase = CurrentAttempt?.Case;
        FileName = inboxItem?.FileName ?? latestCase?.OriginalName ?? Path.GetFileName(sourcePath);
        DeviceId = inboxItem?.DeviceId ?? latestCase?.DeviceId ?? "无法识别";
        LogTime = inboxItem?.LogTime ?? latestCase?.LogTime ?? DateTime.MinValue;
        SourceExists = File.Exists(sourcePath);
        HasActiveTask = Attempts.Any(x => x.IsActive);
        LastActivityTime = Attempts.Select(x => x.ActivityTime).Append(LogTime).Max();
        ActivityTimes = Attempts.Select(x => x.ActivityTime).Append(LogTime).ToArray();
        SearchText = string.Join('\n', new[] { FileName, DeviceId, SourcePath }.Concat(Attempts.SelectMany(x => new[] { x.Case.DisplayName, x.PluginName })));

        if (inboxItem is { IsValidArchive: false })
        {
            StageKey = "invalid";
            StatusText = "无效日志";
            StatusValue = null;
        }
        else if (HasActiveTask)
        {
            StageKey = "active";
            StatusText = CurrentAttempt?.Task?.Status == AnalysisTaskStatus.Running || CurrentAttempt?.Case.Status == CaseStatus.Running ? "分析中" : "等待分析";
            StatusValue = CurrentAttempt?.StatusValue;
        }
        else if (CurrentAttempt is null)
        {
            StageKey = inboxItem?.IsValidArchive == false ? "invalid" : "pending";
            StatusText = inboxItem?.IsValidArchive == false ? "无效日志" : "待分析";
            StatusValue = null;
        }
        else if (CurrentAttempt.Case.Status == CaseStatus.Completed && CurrentAttempt.Report?.IsAvailable == true)
        {
            StageKey = "completed";
            StatusText = "已完成";
            StatusValue = CaseStatus.Completed;
        }
        else
        {
            StageKey = "failed";
            StatusText = CurrentAttempt.Case.Status == CaseStatus.Completed ? "报告不可用" : CurrentAttempt.Task?.Status == AnalysisTaskStatus.Cancelled ? "已取消" : "失败";
            StatusValue = CaseStatus.Failed;
        }

        CanExecutePrimary = (StageKey is "pending" or "failed" && SourceExists) || StageKey == "completed";
        PrimaryActionText = StageKey switch
        {
            "pending" => "开始分析",
            "failed" when SourceExists => "重新分析",
            "completed" => "打开报告",
            "active" => "分析进行中",
            "invalid" => "无法分析",
            _ => "源日志不存在"
        };
    }

    public string SourcePath { get; }
    public LogInboxItem? InboxItem { get; }
    public ObservableCollection<AnalysisAttemptViewModel> Attempts { get; }
    public AnalysisAttemptViewModel? CurrentAttempt { get; }
    public ReportSummary? LatestAvailableReport { get; }
    public string FileName { get; }
    public string DeviceId { get; }
    public DateTime LogTime { get; }
    public bool SourceExists { get; }
    public bool HasActiveTask { get; }
    public DateTime LastActivityTime { get; }
    public IReadOnlyList<DateTime> ActivityTimes { get; }
    public string SearchText { get; }
    public string StageKey { get; }
    public string StatusText { get; }
    public object? StatusValue { get; }
    public bool CanExecutePrimary { get; }
    public string PrimaryActionText { get; }
    public bool CanOpenReport => LatestAvailableReport is not null;
    public bool CanOpenExtractDirectory => CurrentAttempt is not null;
    public AnalysisTask? ActiveTask => Attempts.Select(x => x.Task).FirstOrDefault(x => x?.Status is AnalysisTaskStatus.Waiting or AnalysisTaskStatus.Running);
    public bool CanCancelAnalysis => ActiveTask is not null;
    public bool CanAnalyzeSingle => SourceExists && !HasActiveTask && StageKey != "invalid";
    public bool IsBulkEligible => SourceExists && StageKey == "pending";
    public bool IsInvalidDeleteEligible => SourceExists && !HasActiveTask && StageKey == "invalid";
    public bool CanDeleteLifecycle => !HasActiveTask && (SourceExists || Attempts.Count > 0);
    public string SingleAnalysisText => StageKey == "pending" ? "分析" : "重新分析";
    public string SourceStateText => SourceExists ? "原始日志可用" : "原始日志已删除或移动";
    public string InboxError => InboxItem?.ErrorMessage ?? string.Empty;
    public bool HasInboxError => !string.IsNullOrWhiteSpace(InboxError);
}

/// <summary>刷新期间按源日志归并案例、任务与报告，构造一次性列表快照。</summary>
internal sealed class AnalysisLogGroupBuilder
{
    public AnalysisLogGroupBuilder(string sourcePath) => SourcePath = sourcePath;
    public string SourcePath { get; }
    public LogInboxItem? InboxItem { get; set; }
    public List<AnalysisAttemptViewModel> Attempts { get; } = new();
    public AnalysisLogGroupViewModel Build() => new(SourcePath, InboxItem, Attempts);
}

internal enum AnalysisSubmissionResult
{
    Submitted,
    Skipped,
    Failed
}
