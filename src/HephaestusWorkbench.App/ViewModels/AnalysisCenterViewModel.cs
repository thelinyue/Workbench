using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using HephaestusWorkbench.Core.Models;
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
    private readonly StorageService _storage;
    private readonly SettingsService _settings;
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
    private bool _cleanupEnabled;
    private int _cleanupRetentionDays = 7;
    private string _cleanupSettingsMessage = string.Empty;
    private int _suppressStateRefresh;
    private int _pendingLoadOperations;
    private bool _disposed;

    public AnalysisCenterViewModel(
        LogInboxService inbox,
        CaseAnalysisService analysis,
        ReportService reportService,
        StorageService storage,
        SettingsService settings,
        ReportsWorkspaceViewModel reports,
        Action<string> openExtractDirectory,
        WorkbenchLogger logger,
        Func<string, bool>? confirmDeleteLifecycle = null)
    {
        _inbox = inbox;
        _analysis = analysis;
        _reportService = reportService;
        _storage = storage;
        _settings = settings;
        Reports = reports;
        _openExtractDirectory = openExtractDirectory;
        _logger = logger;
        _confirmDeleteLifecycle = confirmDeleteLifecycle ?? (message => Wpf.MessageBox.Show(message, "删除全部数据", Wpf.MessageBoxButton.YesNo, Wpf.MessageBoxImage.Warning) == Wpf.MessageBoxResult.Yes);

        RefreshCommand = new DelegateCommand(() => { _operationMessage = null; _ = LoadAsync(); }, () => !IsBulkOperationActive);
        OpenRowReportCommand = new DelegateCommand(parameter => _ = OpenRowReportAsync(parameter as AnalysisLogGroupViewModel), parameter => parameter is AnalysisLogGroupViewModel { CanOpenReport: true });
        AnalyzeAllPendingCommand = new DelegateCommand(() => _ = AnalyzeAllPendingAsync(), () => !IsBulkOperationActive && BulkEligibleCount > 0);
        DeleteInvalidCommand = new DelegateCommand(() => _ = DeleteInvalidAsync(), () => !IsBulkOperationActive && InvalidDeleteCount > 0);
        AnalyzeSingleCommand = new DelegateCommand(parameter => _ = AnalyzeSingleAsync(parameter as AnalysisLogGroupViewModel), CanAnalyzeSingle);
        OpenExtractDirectoryCommand = new DelegateCommand(OpenExtractDirectory, CanOpenExtractDirectory);
        OpenReportFolderCommand = new DelegateCommand(OpenReportFolder, parameter => ResolveAttempt(parameter)?.Report is not null);
        DeleteLifecycleCommand = new DelegateCommand(parameter => _ = DeleteLifecycleAsync(parameter as AnalysisLogGroupViewModel), parameter => parameter is AnalysisLogGroupViewModel item && item.CanDeleteLifecycle && !IsBulkOperationActive);
        CleanupExpiredCommand = new DelegateCommand(() => _ = CleanupExpiredAsync(), () => !IsBulkOperationActive && CleanupEnabled);
        SaveCleanupSettingsCommand = new DelegateCommand(() => _ = SaveCleanupSettingsAsync(), () => !IsBulkOperationActive);

        _inbox.ItemsChanged += OnSourceStateChanged;
        _inbox.ConfigurationChanged += OnSourceStateChanged;
        _analysis.StateChanged += OnSourceStateChanged;
    }

    public ReportsWorkspaceViewModel Reports { get; }
    public ObservableCollection<AnalysisLogGroupViewModel> Items { get; } = new();
    public ICommand RefreshCommand { get; }
    public ICommand OpenRowReportCommand { get; }
    public ICommand AnalyzeAllPendingCommand { get; }
    public ICommand DeleteInvalidCommand { get; }
    public ICommand AnalyzeSingleCommand { get; }
    public ICommand OpenExtractDirectoryCommand { get; }
    public ICommand OpenReportFolderCommand { get; }
    public ICommand DeleteLifecycleCommand { get; }
    public ICommand CleanupExpiredCommand { get; }
    public ICommand SaveCleanupSettingsCommand { get; }

    public bool ShowEmptyState => Items.Count == 0;
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
    public string TotalSpace { get; private set; } = "计算中";
    public string ReleasableSpace { get; private set; } = "计算中";
    public string LogSpace { get; private set; } = "计算中";
    public string ExtractSpace { get; private set; } = "计算中";
    public string ReportSpace { get; private set; } = "计算中";
    public bool CleanupEnabled
    {
        get => _cleanupEnabled;
        set
        {
            if (!SetProperty(ref _cleanupEnabled, value)) return;
            ((DelegateCommand)CleanupExpiredCommand).RaiseCanExecuteChanged();
        }
    }
    public int CleanupRetentionDays { get => _cleanupRetentionDays; set => SetProperty(ref _cleanupRetentionDays, value); }
    public string CleanupSettingsMessage { get => _cleanupSettingsMessage; private set => SetProperty(ref _cleanupSettingsMessage, value); }

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
            var storageTask = _storage.GetSummaryAsync();
            var cleanupEnabledTask = _settings.GetManualCleanupEnabledAsync();
            var cleanupDaysTask = _settings.GetCleanupRetentionDaysAsync();
            await Task.WhenAll(casesTask, tasksTask, reportsTask, storageTask, cleanupEnabledTask, cleanupDaysTask);
            var storage = storageTask.Result;
            TotalSpace = ViewModelFormatting.Size(storage.TotalBytes);
            ReleasableSpace = ViewModelFormatting.Size(storage.ReleasableBytes);
            LogSpace = ViewModelFormatting.Size(storage.LogBytes);
            ExtractSpace = ViewModelFormatting.Size(storage.ExtractBytes);
            ReportSpace = ViewModelFormatting.Size(storage.ReportBytes);
            CleanupEnabled = cleanupEnabledTask.Result;
            CleanupRetentionDays = cleanupDaysTask.Result;
            OnPropertyChanged(nameof(TotalSpace));
            OnPropertyChanged(nameof(ReleasableSpace));
            OnPropertyChanged(nameof(LogSpace));
            OnPropertyChanged(nameof(ExtractSpace));
            OnPropertyChanged(nameof(ReportSpace));
            OnPropertyChanged(nameof(CleanupEnabled));
            OnPropertyChanged(nameof(CleanupRetentionDays));

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
        Reports.IsAnalysisListVisible = true;
        SelectedItem = group;
        SelectedAttempt = group.Attempts.First(x => string.Equals(x.Case.Id, caseId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task SelectSourceAsync(string sourcePath)
    {
        await LoadAsync();
        Reports.IsAnalysisListVisible = true;
        SelectedItem = _allItems.FirstOrDefault(x => PathsEqual(x.SourcePath, sourcePath));
    }

    public Task<bool> OpenCaseReportAsync(string caseId) => Reports.OpenCaseReportAsync(caseId);

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
        RaiseCommands();
    }

    private async Task OpenRowReportAsync(AnalysisLogGroupViewModel? item)
    {
        if (item?.LatestAvailableReport is { IsAvailable: true } report)
        {
            await Reports.OpenReportAsync(report);
            return;
        }
        Message = "该日志暂无可用报告。";
    }

    private bool CanAnalyzeSingle(object? parameter)
        => parameter is AnalysisLogGroupViewModel item
            && !IsBulkOperationActive
            && item.CanAnalyzeSingle
            && !_submittingSources.Contains(item.SourcePath);

    private async Task AnalyzeSingleAsync(AnalysisLogGroupViewModel? item)
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
            if (submission.Result == AnalysisSubmissionResult.Submitted
                && submission.Task is { CaseId: var caseId, ReportPath: not null })
            {
                await LoadAsync();
                if (await Reports.OpenCaseReportAsync(caseId))
                    feedback = $"重新分析完成，已打开最新报告：{item.FileName}";
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
            AnalysisLogGroupViewModel group => group.CurrentAttempt,
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
        Reports.CloseCaseTabs(item.Attempts.Select(x => x.Case.Id));
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
            + "报告文件、原始日志、解压目录及全部数据库记录（案例、任务、报告、报告会话）都将被删除，此操作不可恢复。";
        if (!_confirmDeleteLifecycle(message)) return;

        BeginBulkOperation();
        Reports.CloseCaseTabs(targets.SelectMany(x => x.Attempts).Select(x => x.Case.Id));
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

    private async Task CleanupExpiredAsync()
    {
        if (IsBulkOperationActive || !CleanupEnabled) return;
        var cutoff = DateTime.Now.AddDays(-CleanupRetentionDays);
        var candidates = Items.SelectMany(x => x.Attempts)
            .Where(x => x.Report is { IsAvailable: true, CreateTime: var time } && time < cutoff
                && x.Task?.Status is not (AnalysisTaskStatus.Waiting or AnalysisTaskStatus.Running))
            .Select(x => x.Case.SourcePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (candidates.Length == 0)
        {
            Message = $"没有找到超过 {CleanupRetentionDays} 天的可清理报告。";
            return;
        }

        var message = $"发现 {candidates.Length} 份报告超过 {CleanupRetentionDays} 天。\n\n"
            + "将删除对应报告文件、原始日志、解压目录及全部数据库记录（案例、任务、报告、报告会话），此操作不可恢复。\n\n是否立即清理？";
        if (!_confirmDeleteLifecycle(message)) return;
        BeginBulkOperation();
        try
        {
            Reports.CloseCaseTabs(Items.SelectMany(x => x.Attempts)
                .Where(x => candidates.Contains(x.Case.SourcePath, StringComparer.OrdinalIgnoreCase))
                .Select(x => x.Case.Id));
            var result = await _analysis.CleanupExpiredAsync(CleanupRetentionDays);
            Message = $"到期报告清理完成：成功 {result.Deleted} 份，跳过 {result.Skipped} 份，失败 {result.Failed} 份。";
        }
        catch (Exception ex)
        {
            Message = $"到期报告清理失败：{ex.Message}";
            _logger.Error("到期报告清理失败", ex);
        }
        finally
        {
            EndBulkOperation();
            await LoadAsync();
        }
    }

    /// <summary>在分析中心就地保存清理策略，保证设置入口与实际清理动作处于同一业务上下文。</summary>
    private async Task SaveCleanupSettingsAsync()
    {
        if (CleanupRetentionDays is < 1 or > 7)
        {
            CleanupSettingsMessage = "清理保留天数必须在 1 到 7 天之间。";
            return;
        }

        try
        {
            await _settings.SetManualCleanupEnabledAsync(CleanupEnabled);
            await _settings.SetCleanupRetentionDaysAsync(CleanupRetentionDays);
            CleanupSettingsMessage = "清理策略已保存。";
            ((DelegateCommand)CleanupExpiredCommand).RaiseCanExecuteChanged();
        }
        catch (Exception ex)
        {
            CleanupSettingsMessage = $"清理策略保存失败：{ex.Message}";
            _logger.Error("清理策略保存失败", ex);
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
            DeleteInvalidCommand, AnalyzeSingleCommand, OpenExtractDirectoryCommand, OpenReportFolderCommand,
            DeleteLifecycleCommand, CleanupExpiredCommand, SaveCleanupSettingsCommand
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
        Reports.Dispose();
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
