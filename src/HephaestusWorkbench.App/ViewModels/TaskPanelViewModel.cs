using System.Collections.ObjectModel;
using System.Windows.Input;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Services;
using Wpf = System.Windows;
using AnalysisTaskStatus = HephaestusWorkbench.Core.Models.TaskStatus;

namespace HephaestusWorkbench.App.ViewModels;

/// <summary>
/// 全局任务面板只承载需要跨页面关注的后台状态。完整历史仍归属于分析中心的日志生命周期，
/// 因此这里只保留全部活动任务和最近十条结束任务，避免重新形成一套独立业务页面。
/// </summary>
public sealed class TaskPanelViewModel : ViewModelBase, IDisposable
{
    private readonly CaseAnalysisService _analysis;
    private readonly Action<string> _openCase;
    private readonly Func<string, bool> _confirmCancel;
    private bool _isOpen;
    private int _activeTaskCount;
    private bool _disposed;

    public TaskPanelViewModel(CaseAnalysisService analysis, Action<string> openCase, Func<string, bool>? confirmCancel = null)
    {
        _analysis = analysis;
        _openCase = openCase;
        _confirmCancel = confirmCancel ?? (message => Wpf.MessageBox.Show(message, "取消分析任务", Wpf.MessageBoxButton.YesNo, Wpf.MessageBoxImage.Question) == Wpf.MessageBoxResult.Yes);
        ToggleCommand = new DelegateCommand(() => IsOpen = !IsOpen);
        CloseCommand = new DelegateCommand(() => IsOpen = false);
        RefreshCommand = new DelegateCommand(() => _ = LoadAsync());
        OpenTaskCommand = new DelegateCommand(parameter => { if (parameter is TaskPanelItemViewModel item) OpenTask(item); });
        CancelTaskCommand = new DelegateCommand(parameter => { if (parameter is TaskPanelItemViewModel item) _ = CancelAsync(item); });
        _analysis.StateChanged += OnStateChanged;
    }

    public ObservableCollection<TaskPanelItemViewModel> Items { get; } = new();
    public ICommand ToggleCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand OpenTaskCommand { get; }
    public ICommand CancelTaskCommand { get; }
    public bool ShowEmptyState => Items.Count == 0;
    public bool HasActiveTasks => ActiveTaskCount > 0;
    public string ButtonToolTip => HasActiveTasks ? $"查看后台任务，当前 {ActiveTaskCount} 个活动任务" : "查看后台任务";

    public bool IsOpen { get => _isOpen; set => SetProperty(ref _isOpen, value); }
    public int ActiveTaskCount
    {
        get => _activeTaskCount;
        private set
        {
            if (!SetProperty(ref _activeTaskCount, value)) return;
            OnPropertyChanged(nameof(HasActiveTasks));
            OnPropertyChanged(nameof(ButtonToolTip));
        }
    }

    public async Task LoadAsync()
    {
        if (_disposed) return;
        var tasks = await _analysis.ListTasksAsync();
        var cases = await _analysis.ListCasesAsync();
        var caseNames = cases.ToDictionary(x => x.Id, x => x.DisplayName, StringComparer.OrdinalIgnoreCase);
        var active = tasks.Where(IsActive).OrderBy(x => x.Status == AnalysisTaskStatus.Running ? 0 : 1).ThenByDescending(x => x.StartTime).ToArray();
        var recent = tasks.Where(x => !IsActive(x)).OrderByDescending(x => x.EndTime ?? x.StartTime ?? DateTime.MinValue).Take(10).ToArray();
        ActiveTaskCount = active.Length;
        Items.Clear();
        foreach (var task in active.Concat(recent))
            Items.Add(new TaskPanelItemViewModel(task, caseNames.GetValueOrDefault(task.CaseId) ?? task.CaseId));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    private async Task CancelAsync(TaskPanelItemViewModel item)
    {
        if (!item.IsActive) return;
        var message = $"确认取消案例“{item.CaseName}”的分析任务吗？\n\n正在运行的插件会收到取消请求。";
        if (!_confirmCancel(message)) return;
        if (!await _analysis.CancelAsync(item.Task.Id))
            Wpf.MessageBox.Show("任务已经结束或当前无法取消。", "无法取消任务", Wpf.MessageBoxButton.OK, Wpf.MessageBoxImage.Information);
        await LoadAsync();
    }

    private void OpenTask(TaskPanelItemViewModel item)
    {
        IsOpen = false;
        _openCase(item.Task.CaseId);
    }

    private void OnStateChanged(object? sender, EventArgs e) => RunOnUi(() => _ = LoadAsync());
    private static bool IsActive(AnalysisTask task) => task.Status is AnalysisTaskStatus.Waiting or AnalysisTaskStatus.Running;

    private static void RunOnUi(Action action)
    {
        var dispatcher = Wpf.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else _ = dispatcher.InvokeAsync(action);
    }

    public void Dispose()
    {
        _disposed = true;
        _analysis.StateChanged -= OnStateChanged;
    }
}

public sealed class TaskPanelItemViewModel
{
    public TaskPanelItemViewModel(AnalysisTask task, string caseName)
    {
        Task = task;
        CaseName = caseName;
    }

    public AnalysisTask Task { get; }
    public string CaseName { get; }
    public string PluginId => Task.PluginId;
    public object StatusValue => Task.Status;
    public bool IsActive => Task.Status is AnalysisTaskStatus.Waiting or AnalysisTaskStatus.Running;
    public bool HasError => !string.IsNullOrWhiteSpace(Task.ErrorMessage);
    public string ErrorMessage => Task.ErrorMessage ?? string.Empty;
    public string TimeText
    {
        get
        {
            if (Task.StartTime is null) return "等待开始";
            var end = Task.EndTime ?? DateTime.Now;
            var duration = end - Task.StartTime.Value;
            return Task.EndTime is null
                ? $"已运行 {FormatDuration(duration)}"
                : $"耗时 {FormatDuration(duration)}";
        }
    }

    private static string FormatDuration(TimeSpan duration)
        => duration.TotalHours >= 1 ? $"{(int)duration.TotalHours} 小时 {duration.Minutes} 分" : duration.TotalMinutes >= 1 ? $"{(int)duration.TotalMinutes} 分 {duration.Seconds} 秒" : $"{Math.Max(0, duration.Seconds)} 秒";
}
