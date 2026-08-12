using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Services;
using Wpf = System.Windows;
using AnalysisTaskStatus = HephaestusWorkbench.Core.Models.TaskStatus;

namespace HephaestusWorkbench.App.ViewModels;

public sealed record AnalysisFilterOption(string? Key, string Name);
public sealed record AnalysisPluginOption(string? Id, string Name);

/// <summary>
/// 分析中心把收件箱文件、案例、后台任务和报告按源日志路径聚合成一个生命周期。
/// 聚合只发生在界面层，不改变现有数据库结构；刷新时始终重新读取业务服务，避免缓存状态与后台任务脱节。
/// </summary>
public sealed class AnalysisCenterViewModel : ViewModelBase, IDisposable
{
    private readonly LogInboxService _inbox;
    private readonly CaseAnalysisService _analysis;
    private readonly ReportService _reportService;
    private readonly Action<string> _openExtractDirectory;
    private readonly WorkbenchLogger _logger;
    private readonly Func<string, bool> _confirmDeleteSource;
    private readonly Func<string, bool> _confirmDeleteLifecycle;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly List<AnalysisLogGroupViewModel> _allItems = new();
    private AnalysisLogGroupViewModel? _selectedItem;
    private AnalysisAttemptViewModel? _selectedAttempt;
    private string _keyword = string.Empty;
    private string _deviceId = string.Empty;
    private AnalysisFilterOption? _selectedStatus;
    private AnalysisPluginOption? _selectedPlugin;
    private DateTime? _startDate;
    private DateTime? _endDate;
    private string _message = string.Empty;
    private string _caseName = string.Empty;
    private bool _disposed;

    public AnalysisCenterViewModel(
        LogInboxService inbox,
        CaseAnalysisService analysis,
        ReportService reportService,
        ReportsWorkspaceViewModel reports,
        Action<string> openExtractDirectory,
        WorkbenchLogger logger,
        Func<string, bool>? confirmDeleteSource = null,
        Func<string, bool>? confirmDeleteLifecycle = null)
    {
        _inbox = inbox;
        _analysis = analysis;
        _reportService = reportService;
        Reports = reports;
        _openExtractDirectory = openExtractDirectory;
        _logger = logger;
        _confirmDeleteSource = confirmDeleteSource ?? (message => Wpf.MessageBox.Show(message, "仅删除原始日志", Wpf.MessageBoxButton.YesNo, Wpf.MessageBoxImage.Warning) == Wpf.MessageBoxResult.Yes);
        _confirmDeleteLifecycle = confirmDeleteLifecycle ?? (message => Wpf.MessageBox.Show(message, "删除全部数据", Wpf.MessageBoxButton.YesNo, Wpf.MessageBoxImage.Warning) == Wpf.MessageBoxResult.Yes);

        StatusOptions = new ObservableCollection<AnalysisFilterOption>
        {
            new(null, "全部状态"),
            new("pending", "待分析"),
            new("active", "等待或分析中"),
            new("completed", "已完成"),
            new("failed", "失败或不可用"),
            new("invalid", "无效日志")
        };
        PluginOptions = new ObservableCollection<AnalysisPluginOption> { new(null, "全部插件") };
        _selectedStatus = StatusOptions[0];
        _selectedPlugin = PluginOptions[0];

        RefreshCommand = new DelegateCommand(() => _ = LoadAsync());
        PrimaryCommand = new DelegateCommand(() => _ = ExecutePrimaryAsync(), () => SelectedItem?.CanExecutePrimary == true);
        RenameCommand = new DelegateCommand(() => _ = RenameAsync(), () => SelectedAttempt is not null && !string.IsNullOrWhiteSpace(CaseName));
        OpenReportCommand = new DelegateCommand(() => _ = OpenSelectedReportAsync(), () => SelectedAttempt?.Report?.IsAvailable == true);
        OpenExtractDirectoryCommand = new DelegateCommand(OpenExtractDirectory, () => SelectedAttempt is not null);
        OpenReportFolderCommand = new DelegateCommand(OpenReportFolder, () => SelectedAttempt?.Report is not null);
        DeleteSourceCommand = new DelegateCommand(() => _ = DeleteSourceAsync(), () => SelectedItem?.SourceExists == true);
        DeleteLifecycleCommand = new DelegateCommand(() => _ = DeleteLifecycleAsync(), () => SelectedItem is { Attempts.Count: > 0, HasActiveTask: false });

        _inbox.ItemsChanged += OnSourceStateChanged;
        _inbox.ConfigurationChanged += OnSourceStateChanged;
        _analysis.StateChanged += OnSourceStateChanged;
    }

    public ReportsWorkspaceViewModel Reports { get; }
    public ObservableCollection<AnalysisLogGroupViewModel> Items { get; } = new();
    public ObservableCollection<AnalysisFilterOption> StatusOptions { get; }
    public ObservableCollection<AnalysisPluginOption> PluginOptions { get; }
    public ICommand RefreshCommand { get; }
    public ICommand PrimaryCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand OpenReportCommand { get; }
    public ICommand OpenExtractDirectoryCommand { get; }
    public ICommand OpenReportFolderCommand { get; }
    public ICommand DeleteSourceCommand { get; }
    public ICommand DeleteLifecycleCommand { get; }

    public bool ShowEmptyState => Items.Count == 0;
    public bool HasSelection => SelectedItem is not null;
    public bool HasSelectedAttempt => SelectedAttempt is not null;
    public string CaseName
    {
        get => _caseName;
        set
        {
            if (SetProperty(ref _caseName, value)) ((DelegateCommand)RenameCommand).RaiseCanExecuteChanged();
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
            CaseName = value?.Case.DisplayName ?? string.Empty;
            OnPropertyChanged(nameof(HasSelectedAttempt));
            RaiseCommands();
        }
    }

    public string Keyword { get => _keyword; set { if (SetProperty(ref _keyword, value)) ApplyFilter(); } }
    public string DeviceId { get => _deviceId; set { if (SetProperty(ref _deviceId, value)) ApplyFilter(); } }
    public AnalysisFilterOption? SelectedStatus { get => _selectedStatus; set { if (SetProperty(ref _selectedStatus, value)) ApplyFilter(); } }
    public AnalysisPluginOption? SelectedPlugin { get => _selectedPlugin; set { if (SetProperty(ref _selectedPlugin, value)) ApplyFilter(); } }
    public DateTime? StartDate { get => _startDate; set { if (SetProperty(ref _startDate, value)) ApplyFilter(); } }
    public DateTime? EndDate { get => _endDate; set { if (SetProperty(ref _endDate, value)) ApplyFilter(); } }

    public async Task InitializeAsync()
    {
        await Reports.InitializeAsync();
        await LoadAsync();
    }

    public async Task LoadAsync()
    {
        await _loadLock.WaitAsync();
        try
        {
            if (_disposed) return;
            var selectedPath = SelectedItem?.SourcePath;
            var selectedCaseId = SelectedAttempt?.Case.Id;
            var casesTask = _analysis.ListCasesAsync();
            var tasksTask = _analysis.ListTasksAsync();
            var reportsTask = _reportService.ListAsync(new ReportQuery());
            await Task.WhenAll(casesTask, tasksTask, reportsTask);

            _allItems.Clear();
            _allItems.AddRange(BuildGroups(_inbox.Items, casesTask.Result, tasksTask.Result, reportsTask.Result));
            RebuildPluginOptions();
            ApplyFilter(selectedPath, selectedCaseId);
            Message = string.Empty;
        }
        catch (Exception ex)
        {
            Message = $"加载分析中心失败：{ex.Message}";
            _logger.Error("加载分析中心失败", ex);
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public async Task SelectCaseAsync(string caseId)
    {
        await LoadAsync();
        var group = _allItems.FirstOrDefault(x => x.Attempts.Any(a => string.Equals(a.Case.Id, caseId, StringComparison.OrdinalIgnoreCase)));
        if (group is null) return;
        Reports.IsLibraryVisible = true;
        ResetFilters();
        SelectedItem = group;
        SelectedAttempt = group.Attempts.First(x => string.Equals(x.Case.Id, caseId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task SelectSourceAsync(string sourcePath)
    {
        await LoadAsync();
        Reports.IsLibraryVisible = true;
        ResetFilters();
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

    private void RebuildPluginOptions()
    {
        var selectedId = SelectedPlugin?.Id;
        var options = _allItems.SelectMany(x => x.Attempts)
            .Where(x => !string.IsNullOrWhiteSpace(x.PluginId))
            .GroupBy(x => x.PluginId!, StringComparer.OrdinalIgnoreCase)
            .Select(x => new AnalysisPluginOption(x.Key, x.Select(a => a.PluginName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? x.Key))
            .OrderBy(x => x.Name)
            .ToArray();
        PluginOptions.Clear();
        PluginOptions.Add(new AnalysisPluginOption(null, "全部插件"));
        foreach (var option in options) PluginOptions.Add(option);
        _selectedPlugin = PluginOptions.FirstOrDefault(x => string.Equals(x.Id, selectedId, StringComparison.OrdinalIgnoreCase)) ?? PluginOptions[0];
        OnPropertyChanged(nameof(SelectedPlugin));
    }

    private void ApplyFilter(string? selectedPath = null, string? selectedCaseId = null)
    {
        selectedPath ??= SelectedItem?.SourcePath;
        selectedCaseId ??= SelectedAttempt?.Case.Id;
        if (StartDate is not null && EndDate is not null && StartDate > EndDate)
        {
            Message = "开始日期不能晚于结束日期。";
            Items.Clear();
            SelectedItem = null;
            OnPropertyChanged(nameof(ShowEmptyState));
            return;
        }

        var keyword = Keyword.Trim();
        var device = DeviceId.Trim();
        var endExclusive = EndDate?.Date.AddDays(1);
        var filtered = _allItems.Where(item =>
            (string.IsNullOrWhiteSpace(keyword) || item.SearchText.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(device) || item.DeviceId.Contains(device, StringComparison.OrdinalIgnoreCase))
            && (SelectedStatus?.Key is null || string.Equals(item.StageKey, SelectedStatus.Key, StringComparison.Ordinal))
            && (SelectedPlugin?.Id is null || item.Attempts.Any(x => string.Equals(x.PluginId, SelectedPlugin.Id, StringComparison.OrdinalIgnoreCase)))
            && item.ActivityTimes.Any(x => (StartDate is null || x >= StartDate.Value.Date)
                && (endExclusive is null || x < endExclusive.Value)));

        Items.Clear();
        foreach (var item in filtered) Items.Add(item);
        var restored = selectedPath is null ? null : Items.FirstOrDefault(x => PathsEqual(x.SourcePath, selectedPath));
        SelectedItem = restored ?? Items.FirstOrDefault();
        if (selectedCaseId is not null && SelectedItem is not null)
            SelectedAttempt = SelectedItem.Attempts.FirstOrDefault(x => string.Equals(x.Case.Id, selectedCaseId, StringComparison.OrdinalIgnoreCase)) ?? SelectedItem.CurrentAttempt;
        Message = string.Empty;
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    private async Task ExecutePrimaryAsync()
    {
        var item = SelectedItem;
        if (item is null) return;
        if (item.CurrentAttempt?.Report is { IsAvailable: true } report && item.StageKey == "completed")
        {
            await Reports.OpenReportAsync(report);
            return;
        }
        if (item.HasActiveTask || !item.SourceExists) return;

        var inspection = await _inbox.InspectFileAsync(item.SourcePath);
        if (!inspection.IsValid || inspection.Item is null)
        {
            Message = inspection.ErrorMessage ?? "日志文件无法分析。";
            return;
        }
        if (await _analysis.StartAsync(inspection.Item) is null)
        {
            Message = "无法创建分析任务，请检查默认插件是否可用。";
            return;
        }
        await SelectSourceAsync(item.SourcePath);
    }

    private async Task RenameAsync()
    {
        if (SelectedAttempt is null || string.IsNullOrWhiteSpace(CaseName)) return;
        var caseId = SelectedAttempt.Case.Id;
        await _analysis.RenameAsync(caseId, CaseName);
        await SelectCaseAsync(caseId);
    }

    private async Task OpenSelectedReportAsync()
    {
        if (SelectedAttempt?.Report is { IsAvailable: true } report) await Reports.OpenReportAsync(report);
    }

    private void OpenExtractDirectory()
    {
        if (SelectedAttempt is not null) _openExtractDirectory(SelectedAttempt.Case.ExtractPath);
    }

    private void OpenReportFolder()
    {
        var report = SelectedAttempt?.Report;
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

    private async Task DeleteSourceAsync()
    {
        var item = SelectedItem;
        if (item is null || !item.SourceExists) return;
        var message = $"确认仅删除原始日志吗？\n\n{item.SourcePath}\n\n案例、解压目录和报告都会保留。";
        if (!_confirmDeleteSource(message)) return;
        var source = item.InboxItem ?? new LogInboxItem
        {
            FilePath = item.SourcePath,
            FileName = item.FileName,
            DeviceId = item.DeviceId,
            LogTime = item.LogTime,
            IsValidArchive = true
        };
        await _inbox.DeleteAsync(source);
        await LoadAsync();
    }

    private async Task DeleteLifecycleAsync()
    {
        var item = SelectedItem;
        if (item is null || item.HasActiveTask) return;
        var reportCount = item.Attempts.Count(x => x.Report is not null);
        var extractPath = item.Attempts.FirstOrDefault()?.Case.ExtractPath ?? "无";
        var message = $"确认删除这份日志的全部数据吗？\n\n原始日志：{item.SourcePath}\n解压目录：{extractPath}\n案例：{item.Attempts.Count} 个\n报告：{reportCount} 个\n\n此操作不可恢复。";
        if (!_confirmDeleteLifecycle(message)) return;
        Reports.CloseCaseTabs(item.Attempts.Select(x => x.Case.Id));
        try
        {
            await _analysis.DeleteLifecycleAsync(item.SourcePath);
            await _inbox.RefreshAsync();
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Message = ex.Message;
            Wpf.MessageBox.Show(ex.Message, "删除失败", Wpf.MessageBoxButton.OK, Wpf.MessageBoxImage.Error);
        }
    }

    private void OnSourceStateChanged(object? sender, EventArgs e) => RunOnUi(() => _ = LoadAsync());

    private void ResetFilters()
    {
        _keyword = string.Empty;
        _deviceId = string.Empty;
        _selectedStatus = StatusOptions[0];
        _selectedPlugin = PluginOptions[0];
        _startDate = null;
        _endDate = null;
        OnPropertyChanged(nameof(Keyword));
        OnPropertyChanged(nameof(DeviceId));
        OnPropertyChanged(nameof(SelectedStatus));
        OnPropertyChanged(nameof(SelectedPlugin));
        OnPropertyChanged(nameof(StartDate));
        OnPropertyChanged(nameof(EndDate));
        ApplyFilter();
    }

    private void RaiseCommands()
    {
        foreach (var command in new[] { PrimaryCommand, RenameCommand, OpenReportCommand, OpenExtractDirectoryCommand, OpenReportFolderCommand, DeleteSourceCommand, DeleteLifecycleCommand })
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
        _disposed = true;
        _inbox.ItemsChanged -= OnSourceStateChanged;
        _inbox.ConfigurationChanged -= OnSourceStateChanged;
        _analysis.StateChanged -= OnSourceStateChanged;
        _loadLock.Dispose();
        Reports.Dispose();
    }
}

/// <summary>单次案例、任务和报告的只读组合，供日志详情呈现完整分析历史。</summary>
public sealed class AnalysisAttemptViewModel
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
public sealed class AnalysisLogGroupViewModel
{
    public AnalysisLogGroupViewModel(string sourcePath, LogInboxItem? inboxItem, IReadOnlyList<AnalysisAttemptViewModel> attempts)
    {
        SourcePath = sourcePath;
        InboxItem = inboxItem;
        Attempts = new ObservableCollection<AnalysisAttemptViewModel>(attempts.OrderByDescending(x => x.ActivityTime));
        CurrentAttempt = Attempts.FirstOrDefault(x => x.IsActive) ?? Attempts.FirstOrDefault();
        var latestCase = CurrentAttempt?.Case;
        FileName = inboxItem?.FileName ?? latestCase?.OriginalName ?? Path.GetFileName(sourcePath);
        DeviceId = inboxItem?.DeviceId ?? latestCase?.DeviceId ?? "无法识别";
        LogTime = inboxItem?.LogTime ?? latestCase?.LogTime ?? DateTime.MinValue;
        SourceExists = File.Exists(sourcePath);
        HasActiveTask = Attempts.Any(x => x.IsActive);
        LastActivityTime = Attempts.Select(x => x.ActivityTime).Append(LogTime).Max();
        ActivityTimes = Attempts.Select(x => x.ActivityTime).Append(LogTime).ToArray();
        SearchText = string.Join('\n', new[] { FileName, DeviceId, SourcePath }.Concat(Attempts.SelectMany(x => new[] { x.Case.DisplayName, x.PluginName })));

        if (inboxItem is { IsValidArchive: false } && Attempts.Count == 0)
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
    public string SourceStateText => SourceExists ? "原始日志可用" : "原始日志已删除或移动";
    public string InboxError => InboxItem?.ErrorMessage ?? string.Empty;
    public bool HasInboxError => !string.IsNullOrWhiteSpace(InboxError);
}

internal sealed class AnalysisLogGroupBuilder
{
    public AnalysisLogGroupBuilder(string sourcePath) => SourcePath = sourcePath;
    public string SourcePath { get; }
    public LogInboxItem? InboxItem { get; set; }
    public List<AnalysisAttemptViewModel> Attempts { get; } = new();
    public AnalysisLogGroupViewModel Build() => new(SourcePath, InboxItem, Attempts);
}
