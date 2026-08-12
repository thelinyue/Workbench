using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using Wpf = System.Windows;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Services;
using AnalysisTaskStatus = HephaestusWorkbench.Core.Models.TaskStatus;

namespace HephaestusWorkbench.App.ViewModels;

/// <summary>
/// 首页工作台模型。除了汇总案例、任务和存储状态，还负责跟踪一个由首页发起的快捷分析任务。
/// 快捷任务使用任务 ID 精确跟踪，其他页面创建的后台任务完成时不会触发报告跳转。
/// </summary>
public sealed class DashboardViewModel : ViewModelBase, IDisposable
{
    private readonly CaseAnalysisService _analysis;
    private readonly StorageService _storage;
    private readonly LogInboxService _inbox;
    private readonly Func<string, Task<bool>> _openReport;
    private readonly Action<string> _openExtractDirectory;
    private readonly WorkbenchLogger _logger;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly object _loadLifecycleSync = new();
    private readonly SemaphoreSlim _trackedTaskLock = new(1, 1);
    private string? _trackedTaskId;
    private string? _trackedCaseId;
    private bool _isQuickAnalysisActive;
    private string _quickStatusMessage = string.Empty;
    private string _quickSourcePath = string.Empty;
    private bool _quickStatusIsError;
    private int _invalidInboxCount;
    private bool _disposed;
    private int _pendingLoadOperations;

    public DashboardViewModel(
        CaseAnalysisService analysis,
        StorageService storage,
        LogInboxService inbox,
        Action openInbox,
        Action openCases,
        Action openSettings,
        Func<string, Task<bool>> openReport,
        Action<string> openExtractDirectory,
        WorkbenchLogger logger)
    {
        _analysis = analysis;
        _storage = storage;
        _inbox = inbox;
        _openReport = openReport;
        _openExtractDirectory = openExtractDirectory;
        _logger = logger;
        OpenSettingsCommand = new DelegateCommand(openSettings);
        OpenInboxCommand = new DelegateCommand(openInbox);
        OpenCasesCommand = new DelegateCommand(openCases);
        _inbox.ConfigurationChanged += OnConfigurationChanged;
        _inbox.ItemsChanged += OnInboxItemsChanged;
        _analysis.StateChanged += OnAnalysisStateChanged;
        _ = LoadAsync();
    }

    public ObservableCollection<HomeLogItemViewModel> RecentLogs { get; } = new();
    public ObservableCollection<AnalysisCase> RecentCases { get; } = new();
    public ObservableCollection<AnalysisTask> CurrentTasks { get; } = new();
    public string UsedSpace { get; private set; } = "计算中";
    public string ReleasableSpace { get; private set; } = "计算中";
    public int CaseCount { get; private set; }
    public bool ShowFirstUseGuide => _inbox.IsUsingDefaultDirectory;
    public ICommand OpenSettingsCommand { get; }
    public ICommand OpenInboxCommand { get; }
    public ICommand OpenCasesCommand { get; }
    public bool ShowNoCases => RecentCases.Count == 0;
    public bool ShowNoTasks => CurrentTasks.Count == 0;
    public bool ShowNoRecentLogs => RecentLogs.Count == 0;
    public bool HasInvalidInboxItems => InvalidInboxCount > 0;
    public bool HasQuickStatus => !string.IsNullOrWhiteSpace(QuickStatusMessage);
    public bool HasQuickSourcePath => !string.IsNullOrWhiteSpace(QuickSourcePath);
    public bool CanStartQuickAnalysis => !IsQuickAnalysisActive;

    public bool IsQuickAnalysisActive
    {
        get => _isQuickAnalysisActive;
        private set
        {
            if (!SetProperty(ref _isQuickAnalysisActive, value)) return;
            OnPropertyChanged(nameof(CanStartQuickAnalysis));
            foreach (var item in RecentLogs) item.SetQuickAnalysisAvailable(!value);
        }
    }

    public string QuickStatusMessage
    {
        get => _quickStatusMessage;
        private set
        {
            if (SetProperty(ref _quickStatusMessage, value)) OnPropertyChanged(nameof(HasQuickStatus));
        }
    }

    public string QuickSourcePath
    {
        get => _quickSourcePath;
        private set
        {
            if (SetProperty(ref _quickSourcePath, value)) OnPropertyChanged(nameof(HasQuickSourcePath));
        }
    }

    public bool QuickStatusIsError
    {
        get => _quickStatusIsError;
        private set => SetProperty(ref _quickStatusIsError, value);
    }

    public int InvalidInboxCount
    {
        get => _invalidInboxCount;
        private set
        {
            if (SetProperty(ref _invalidInboxCount, value)) OnPropertyChanged(nameof(HasInvalidInboxItems));
        }
    }

    /// <summary>处理文件选择器选择的单个文件；校验和任务创建都在服务层完成。</summary>
    public async Task AnalyzeSelectedFileAsync(string path)
    {
        if (IsQuickAnalysisActive)
        {
            SetQuickStatus("已有快捷分析任务正在等待或运行，请等待任务结束。", isError: true);
            return;
        }

        IsQuickAnalysisActive = true;
        QuickSourcePath = path;
        SetQuickStatus("正在检查日志文件…", isError: false);
        var inspection = await _inbox.InspectFileAsync(path);
        if (!inspection.IsValid || inspection.Item is null)
        {
            var message = inspection.ErrorMessage ?? "日志文件无法分析。";
            _logger.Error($"快捷分析日志校验失败：{message} 路径：{path}");
            FinishQuickAnalysis(message, isError: true);
            return;
        }

        QuickSourcePath = inspection.Item.FilePath;
        try
        {
            var existingCase = (await _analysis.ListCasesAsync()).FirstOrDefault(x => PathsEqual(x.SourcePath, inspection.Item.FilePath));
            if (existingCase?.Status == CaseStatus.Completed && !string.IsNullOrWhiteSpace(existingCase.ReportPath))
            {
                IsQuickAnalysisActive = false;
                SetQuickStatus("该日志已有分析报告，正在打开…", isError: false);
                if (await _openReport(existingCase.Id))
                {
                    SetQuickStatus("报告已打开。", isError: false);
                }
                else
                {
                    _logger.Error($"打开所选日志的已有报告失败：案例 {existingCase.Id}，日志 {inspection.Item.FilePath}");
                    SetQuickStatus("已有报告不存在或无法打开，请到分析中心查看详情。", isError: true);
                }
                return;
            }
            if (existingCase?.Status is CaseStatus.Ready or CaseStatus.Running)
            {
                FinishQuickAnalysis("该日志已有等待或运行中的分析任务，请勿重复提交。", isError: true);
                return;
            }
            await StartQuickAnalysisCoreAsync(inspection.Item);
        }
        catch (Exception ex)
        {
            _logger.Error($"检查日志已有分析记录失败：{inspection.Item.FilePath}", ex);
            FinishQuickAnalysis($"读取分析记录失败：{ex.Message}", isError: true);
        }
    }

    /// <summary>拖放入口只接受一个文件，避免用户误以为当前版本支持批量分析。</summary>
    public Task AnalyzeDroppedFilesAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count != 1)
        {
            SetQuickStatus("一次只能拖入一个 .tgz 日志文件。", isError: true);
            return Task.CompletedTask;
        }
        return AnalyzeSelectedFileAsync(paths[0]);
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
            var cases = await _analysis.ListCasesAsync();
            var tasks = await _analysis.ListTasksAsync();
            var summary = await _storage.GetSummaryAsync();
            ApplyRecentLogs(cases, tasks);
            RecentCases.Clear();
            foreach (var item in cases.Take(8)) RecentCases.Add(item);
            CurrentTasks.Clear();
            foreach (var item in tasks.Where(x => x.Status is not AnalysisTaskStatus.Completed and not AnalysisTaskStatus.Failed and not AnalysisTaskStatus.Cancelled).Take(8)) CurrentTasks.Add(item);
            InvalidInboxCount = _inbox.InvalidItemCount;
            UsedSpace = ViewModelFormatting.Size(summary.TotalBytes);
            ReleasableSpace = ViewModelFormatting.Size(summary.ReleasableBytes);
            CaseCount = summary.CaseCount;
            OnPropertyChanged(nameof(UsedSpace));
            OnPropertyChanged(nameof(ReleasableSpace));
            OnPropertyChanged(nameof(CaseCount));
            OnPropertyChanged(nameof(ShowNoCases));
            OnPropertyChanged(nameof(ShowNoTasks));
        }
        catch (Exception ex)
        {
            _logger.Error("刷新首页日志和任务状态失败", ex);
        }
        finally
        {
            _loadLock.Release();
            Interlocked.Decrement(ref _pendingLoadOperations);
        }
    }

    private void ApplyRecentLogs(IReadOnlyList<AnalysisCase> cases, IReadOnlyList<AnalysisTask> tasks)
    {
        RecentLogs.Clear();
        foreach (var inboxItem in _inbox.Items.Where(x => x.IsValidArchive).OrderByDescending(x => x.LogTime).Take(5))
        {
            var latestCase = cases.FirstOrDefault(x => PathsEqual(x.SourcePath, inboxItem.FilePath));
            var latestTask = latestCase is null ? null : tasks.FirstOrDefault(x => x.CaseId == latestCase.Id);
            var row = new HomeLogItemViewModel(inboxItem, latestCase, latestTask, ExecuteRecentLogAsync, _openExtractDirectory);
            row.SetQuickAnalysisAvailable(!IsQuickAnalysisActive);
            RecentLogs.Add(row);
        }
        OnPropertyChanged(nameof(ShowNoRecentLogs));
    }

    private async Task ExecuteRecentLogAsync(HomeLogItemViewModel row)
    {
        try
        {
            if (row.CanOpenReport && row.CaseId is not null)
            {
                QuickSourcePath = row.Item.FilePath;
                SetQuickStatus("正在打开已有报告…", isError: false);
                if (await _openReport(row.CaseId))
                {
                    SetQuickStatus("报告已打开。", isError: false);
                }
                else
                {
                    var message = "对应报告不存在或无法打开，请到分析中心查看详情。";
                    _logger.Error($"首页打开已有报告失败：案例 {row.CaseId}，日志 {row.Item.FilePath}");
                    SetQuickStatus(message, isError: true);
                }
                return;
            }

            if (!row.CanAnalyze || IsQuickAnalysisActive) return;
            IsQuickAnalysisActive = true;
            QuickSourcePath = row.Item.FilePath;
            await StartQuickAnalysisCoreAsync(row.Item);
        }
        catch (Exception ex)
        {
            _logger.Error($"首页日志操作失败：{row.Item.FilePath}", ex);
            FinishQuickAnalysis($"日志操作失败：{ex.Message}", isError: true);
        }
    }

    private async Task StartQuickAnalysisCoreAsync(LogInboxItem item)
    {
        SetQuickStatus("正在创建分析任务…", isError: false);
        try
        {
            var task = await _analysis.StartAsync(item);
            if (task is null)
            {
                FinishQuickAnalysis("无法创建分析任务，请检查分析插件是否可用。", isError: true);
                return;
            }

            _trackedTaskId = task.Id;
            _trackedCaseId = task.CaseId;
            SetQuickStatus("任务已创建，正在等待分析…", isError: false);
            await LoadAsync();
            // 插件可能很快完成，因此任务 ID 写入后立即查询一次，补偿可能早于返回发生的状态事件。
            await RefreshTrackedTaskAsync();
        }
        catch (Exception ex)
        {
            _logger.Error($"快捷分析任务创建失败：{item.FilePath}", ex);
            FinishQuickAnalysis($"创建分析任务失败：{ex.Message}", isError: true);
        }
    }

    private async Task RefreshTrackedTaskAsync()
    {
        await _trackedTaskLock.WaitAsync();
        try
        {
            var taskId = _trackedTaskId;
            if (taskId is null) return;
            var task = await _analysis.GetTaskAsync(taskId);
            if (task is null)
            {
                _trackedTaskId = null;
                _trackedCaseId = null;
                FinishQuickAnalysis("快捷分析任务不存在，请到右上角任务面板查看详情。", isError: true);
                return;
            }

            switch (task.Status)
            {
                case AnalysisTaskStatus.Waiting:
                    SetQuickStatus("任务正在等待分析…", isError: false);
                    break;
                case AnalysisTaskStatus.Running:
                    SetQuickStatus("正在分析日志，请稍候…", isError: false);
                    break;
                case AnalysisTaskStatus.Completed:
                {
                    var caseId = _trackedCaseId ?? task.CaseId;
                    _trackedTaskId = null;
                    _trackedCaseId = null;
                    IsQuickAnalysisActive = false;
                    SetQuickStatus("分析完成，正在打开报告…", isError: false);
                    await LoadAsync();
                    bool opened;
                    try
                    {
                        opened = await _openReport(caseId);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"快捷分析完成后打开报告异常：案例 {caseId}", ex);
                        SetQuickStatus($"分析已完成，但打开报告失败：{ex.Message}", isError: true);
                        break;
                    }
                    if (opened)
                    {
                        SetQuickStatus("报告已打开。", isError: false);
                    }
                    else
                    {
                        var message = "分析已完成，但报告不存在或无法打开，请到分析中心查看详情。";
                        _logger.Error($"快捷分析完成后打开报告失败：案例 {caseId}");
                        SetQuickStatus(message, isError: true);
                    }
                    break;
                }
                case AnalysisTaskStatus.Failed:
                case AnalysisTaskStatus.Cancelled:
                    _trackedTaskId = null;
                    _trackedCaseId = null;
                    FinishQuickAnalysis(task.ErrorMessage ?? (task.Status == AnalysisTaskStatus.Cancelled ? "分析任务已取消。" : "分析失败，请查看任务详情。"), isError: true);
                    await LoadAsync();
                    break;
            }
        }
        finally
        {
            _trackedTaskLock.Release();
        }
    }

    private void OnConfigurationChanged(object? sender, EventArgs e) => RunOnUi(() =>
    {
        OnPropertyChanged(nameof(ShowFirstUseGuide));
        _ = LoadAsync();
    });

    private void OnInboxItemsChanged(object? sender, EventArgs e) => RunOnUi(() => _ = LoadAsync());

    private void OnAnalysisStateChanged(object? sender, EventArgs e) => RunOnUi(() => _ = HandleAnalysisStateChangedAsync());

    private async Task HandleAnalysisStateChangedAsync()
    {
        try
        {
            await LoadAsync();
            await RefreshTrackedTaskAsync();
        }
        catch (Exception ex)
        {
            _logger.Error("刷新快捷分析任务状态失败", ex);
            FinishQuickAnalysis($"读取任务状态失败：{ex.Message}", isError: true);
        }
    }

    private void FinishQuickAnalysis(string message, bool isError)
    {
        IsQuickAnalysisActive = false;
        SetQuickStatus(message, isError);
    }

    private void SetQuickStatus(string message, bool isError)
    {
        QuickStatusIsError = isError;
        QuickStatusMessage = message;
    }

    private static bool PathsEqual(string first, string second)
    {
        try
        {
            return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Wpf.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else _ = dispatcher.InvokeAsync(action);
    }

    public void Dispose()
    {
        lock (_loadLifecycleSync) _disposed = true;
        _inbox.ConfigurationChanged -= OnConfigurationChanged;
        _inbox.ItemsChanged -= OnInboxItemsChanged;
        _analysis.StateChanged -= OnAnalysisStateChanged;
        while (Volatile.Read(ref _pendingLoadOperations) > 0)
            Thread.Sleep(10);
        _loadLock.Dispose();
        _trackedTaskLock.Dispose();
    }
}

/// <summary>
/// 首页最近日志的一行展示模型。它把日志、最新案例和对应任务折叠成一个明确的主操作，
/// 避免视图层自行判断“分析、等待、查看报告或重新分析”。
/// </summary>
public sealed class HomeLogItemViewModel
{
    private readonly bool _hasOperation;
    private readonly DelegateCommand _primaryCommand;
    private bool _quickAnalysisAvailable;

    public HomeLogItemViewModel(
        LogInboxItem item,
        AnalysisCase? latestCase,
        AnalysisTask? latestTask,
        Func<HomeLogItemViewModel, Task> execute,
        Action<string> openExtractDirectory)
    {
        Item = item;
        CaseId = latestCase?.Id;
        ExtractPath = latestCase?.ExtractPath;
        HasExtractDirectory = !string.IsNullOrWhiteSpace(ExtractPath) && Directory.Exists(ExtractPath);
        var taskStatus = latestTask?.Status;
        State = (object?)taskStatus ?? latestCase?.Status;
        CanOpenReport = latestCase?.Status == CaseStatus.Completed && !string.IsNullOrWhiteSpace(latestCase.ReportPath);
        CanAnalyze = latestCase is null
            || latestCase.Status == CaseStatus.Failed
            || taskStatus is AnalysisTaskStatus.Failed or AnalysisTaskStatus.Cancelled;
        ActionText = CanOpenReport
            ? "查看报告"
            : CanAnalyze && latestCase is not null
                ? "重新分析"
                : CanAnalyze
                    ? "分析并查看"
                    : taskStatus == AnalysisTaskStatus.Running || latestCase?.Status == CaseStatus.Running
                        ? "分析中"
                        : "等待分析";
        StatusText = taskStatus switch
        {
            AnalysisTaskStatus.Waiting => "等待分析",
            AnalysisTaskStatus.Running => "分析中",
            AnalysisTaskStatus.Completed => "已完成",
            AnalysisTaskStatus.Failed => "失败",
            AnalysisTaskStatus.Cancelled => "已取消",
            _ => latestCase?.Status switch
            {
                CaseStatus.Completed => "已完成",
                CaseStatus.Failed => "失败",
                CaseStatus.Running => "分析中",
                CaseStatus.Ready or CaseStatus.Created => "等待分析",
                _ => "待分析"
            }
        };
        DetailMessage = latestTask?.ErrorMessage ?? latestCase?.ErrorMessage ?? string.Empty;
        _hasOperation = CanAnalyze || CanOpenReport;
        _primaryCommand = new DelegateCommand(() => _ = execute(this), () => _quickAnalysisAvailable && _hasOperation);
        PrimaryCommand = _primaryCommand;
        OpenExtractDirectoryCommand = new DelegateCommand(() =>
        {
            if (ExtractPath is not null) openExtractDirectory(ExtractPath);
        });
    }

    public LogInboxItem Item { get; }
    public string FileName => Item.FileName;
    public string DeviceId => Item.DeviceId;
    public DateTime LogTime => Item.LogTime;
    public string FileSizeText => Item.FileSizeText;
    public string? CaseId { get; }
    public string? ExtractPath { get; }
    public bool HasExtractDirectory { get; }
    public object? State { get; }
    public string StatusText { get; }
    public string DetailMessage { get; }
    public bool HasDetailMessage => !string.IsNullOrWhiteSpace(DetailMessage);
    public bool CanAnalyze { get; }
    public bool CanOpenReport { get; }
    public string ActionText { get; }
    public ICommand PrimaryCommand { get; }
    public ICommand OpenExtractDirectoryCommand { get; }

    public void SetQuickAnalysisAvailable(bool available)
    {
        _quickAnalysisAvailable = available;
        _primaryCommand.RaiseCanExecuteChanged();
    }
}
