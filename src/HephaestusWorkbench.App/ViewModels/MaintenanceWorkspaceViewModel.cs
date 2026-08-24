using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Repositories;
using HephaestusWorkbench.Core.Services;
using HephaestusWorkbench.Data;

namespace HephaestusWorkbench.App.ViewModels;

/// <summary>
/// 受控维护窗口的状态模型。窗口只展示宿主生成的不可编辑计划，并把用户确认交给 Executor 再次复核；
/// 复制命令仅用于人工核对，永远不会把复制出的 shell 文本重新送入执行通道。
/// </summary>
public sealed class MaintenanceWorkspaceViewModel : ViewModelBase, IDisposable
{
    private const int HistoryLimit = 200;
    private readonly string _deviceId;
    private readonly ExecutionPlan? _plan;
    private readonly IMaintenanceExecutor _executor;
    private readonly IMaintenanceOperationRepository _operations;
    private readonly DataPaths _paths;
    private readonly Action<string> _clipboardWriter;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _planAcknowledged;
    private string _confirmationText = string.Empty;
    private bool _isExecuting;
    private MaintenanceOperationStatus? _executionStatus;
    private string _statusMessage = string.Empty;
    private string _errorMessage = string.Empty;
    private string _outputTitle = "输出详情";
    private string _outputText = string.Empty;
    private int _disposed;

    public MaintenanceWorkspaceViewModel(
        string deviceId,
        ExecutionPlan? plan,
        IMaintenanceExecutor executor,
        IMaintenanceOperationRepository operations,
        DataPaths paths,
        Action<string> clipboardWriter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(clipboardWriter);

        _deviceId = deviceId;
        _plan = plan;
        _executor = executor;
        _operations = operations;
        _paths = paths;
        _clipboardWriter = clipboardWriter;

        PlanSteps = new ObservableCollection<MaintenancePlanStepViewModel>(
            plan?.Steps.OrderBy(step => step.Index).Select(step => new MaintenancePlanStepViewModel(step))
            ?? []);
        History = [];

        ExecuteCommand = new DelegateCommand(() => _ = ExecuteAsync(), () => CanExecute);
        CopyCommandCommand = new DelegateCommand(parameter =>
        {
            if (parameter is MaintenancePlanStepViewModel step) CopyCommand(step);
        });
        LoadStdoutCommand = new DelegateCommand(parameter =>
        {
            if (parameter is MaintenanceHistoryStepViewModel step) _ = LoadStdoutAsync(step);
        });
        LoadStderrCommand = new DelegateCommand(parameter =>
        {
            if (parameter is MaintenanceHistoryStepViewModel step) _ = LoadStderrAsync(step);
        });

        Initialization = LoadAsync();
    }

    public string DeviceId => _deviceId;
    public ExecutionPlan? Plan => _plan;
    public bool HasPlan => _plan is not null;
    public bool RequiresConfirmationText => _plan?.RiskLevel == MaintenanceRiskLevel.High;
    public string PlanRiskText => _plan?.RiskLevel switch
    {
        MaintenanceRiskLevel.High => "高风险",
        MaintenanceRiskLevel.ReadOnly => "只读",
        _ => "无待执行计划"
    };
    public string PlanSummary => _plan is null
        ? "当前以历史模式打开，仅查看此设备的维护记录。"
        : $"工作流 {_plan.WorkflowId} · 目标 {_plan.Target.DisplayName} · 扩展 {_plan.ExtensionId} {_plan.ExtensionVersion}";

    public ObservableCollection<MaintenancePlanStepViewModel> PlanSteps { get; }
    public ObservableCollection<MaintenanceHistoryItemViewModel> History { get; }
    public Task Initialization { get; }
    public ICommand ExecuteCommand { get; }
    public ICommand CopyCommandCommand { get; }
    public ICommand LoadStdoutCommand { get; }
    public ICommand LoadStderrCommand { get; }

    public bool PlanAcknowledged
    {
        get => _planAcknowledged;
        set
        {
            if (!SetProperty(ref _planAcknowledged, value)) return;
            NotifyExecutionAvailability();
        }
    }

    public string ConfirmationText
    {
        get => _confirmationText;
        set
        {
            if (!SetProperty(ref _confirmationText, value ?? string.Empty)) return;
            NotifyExecutionAvailability();
        }
    }

    public bool IsExecuting
    {
        get => _isExecuting;
        private set
        {
            if (!SetProperty(ref _isExecuting, value)) return;
            NotifyExecutionAvailability();
        }
    }

    public bool CanExecute =>
        _plan is not null
        && PlanAcknowledged
        && !IsExecuting
        && (!RequiresConfirmationText || string.Equals(ConfirmationText, _plan.Target.DisplayName, StringComparison.Ordinal));

    public MaintenanceOperationStatus? ExecutionStatus
    {
        get => _executionStatus;
        private set
        {
            if (!SetProperty(ref _executionStatus, value)) return;
            OnPropertyChanged(nameof(ExecutionStatusText));
        }
    }

    public string ExecutionStatusText => ExecutionStatus is null ? "尚未执行" : MaintenanceStatusText.Operation(ExecutionStatus.Value);

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public string OutputTitle
    {
        get => _outputTitle;
        private set => SetProperty(ref _outputTitle, value);
    }

    public string OutputText
    {
        get => _outputText;
        private set => SetProperty(ref _outputText, value);
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        try
        {
            await LoadHistoryAsync(linked.Token);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            StatusMessage = "已取消窗口等待。";
        }
        catch (Exception exception)
        {
            ErrorMessage = $"加载维护历史失败：{exception.Message}";
        }
    }

    /// <summary>
    /// 执行请求只携带不可变 ExecutionPlan 和确认文本。界面不解析或重组命令，实际身份复核、停止和断线语义由 Executor 保证。
    /// </summary>
    public async Task ExecuteAsync()
    {
        if (_plan is null)
        {
            ErrorMessage = "当前窗口仅用于查看历史，没有可执行计划。";
            return;
        }

        if (!CanExecute)
        {
            ErrorMessage = RequiresConfirmationText
                ? "请先核对不可编辑计划，并准确输入目标设备名称。"
                : "请先勾选“已核对不可编辑计划”。";
            return;
        }

        ErrorMessage = string.Empty;
        StatusMessage = "正在提交受控维护计划……";
        IsExecuting = true;
        try
        {
            var request = new MaintenanceExecutionRequest
            {
                Plan = _plan,
                ConfirmationDisplayName = RequiresConfirmationText ? ConfirmationText : null,
                Automatic = false
            };

            await foreach (var operationEvent in _executor.ExecuteAsync(request, _lifetime.Token))
                ApplyExecutionEvent(operationEvent);

            if (_lifetime.IsCancellationRequested)
                StatusMessage = "已取消窗口等待；正在执行的高风险步骤由执行器按安全边界处理。";
            else
                await LoadHistoryAsync(CancellationToken.None);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            StatusMessage = "已取消窗口等待；正在执行的高风险步骤由执行器按安全边界处理。";
        }
        catch (Exception exception)
        {
            ErrorMessage = $"执行维护计划失败：{exception.Message}";
            if (!_lifetime.IsCancellationRequested)
                await TryRefreshHistoryAsync();
        }
        finally
        {
            IsExecuting = false;
        }
    }

    public void CopyCommand(MaintenancePlanStepViewModel step)
    {
        ArgumentNullException.ThrowIfNull(step);
        try
        {
            _clipboardWriter(step.CommandText);
            ErrorMessage = string.Empty;
            StatusMessage = "命令已复制。复制内容仅供核对，不会由界面直接执行。";
        }
        catch (Exception exception)
        {
            ErrorMessage = $"复制命令失败：{exception.Message}";
        }
    }

    public Task LoadStdoutAsync(MaintenanceHistoryStepViewModel step) =>
        LoadOutputAsync(step, step.StdoutPath, "stdout");

    public Task LoadStderrAsync(MaintenanceHistoryStepViewModel step) =>
        LoadOutputAsync(step, step.StderrPath, "stderr");

    /// <summary>窗口关闭只取消界面仍在等待的异步枚举；高风险步骤能否停止仍由 Executor 的步骤边界决定。</summary>
    public void CancelPendingOperations()
    {
        if (!_lifetime.IsCancellationRequested) _lifetime.Cancel();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private async Task LoadHistoryAsync(CancellationToken cancellationToken)
    {
        var recent = await _operations.ListRecentAsync(HistoryLimit, cancellationToken);
        var filtered = recent
            .Where(operation => string.Equals(operation.DeviceId, _deviceId, StringComparison.Ordinal))
            .Select(operation => new MaintenanceHistoryItemViewModel(operation))
            .ToArray();

        History.Clear();
        foreach (var operation in filtered) History.Add(operation);
    }

    private async Task TryRefreshHistoryAsync()
    {
        try
        {
            await LoadHistoryAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            ErrorMessage = $"刷新维护历史失败：{exception.Message}";
        }
    }

    private void ApplyExecutionEvent(MaintenanceOperationEvent operationEvent)
    {
        if (operationEvent.OperationStatus is { } operationStatus)
            ExecutionStatus = operationStatus;

        if (_plan is not null && operationEvent.StepId is { } persistedStepId && operationEvent.StepStatus is { } stepStatus)
        {
            var currentPlanPrefix = _plan.Id + ":";
            if (persistedStepId.StartsWith(currentPlanPrefix, StringComparison.Ordinal))
            {
                var executionStepId = persistedStepId[currentPlanPrefix.Length..];
                var step = PlanSteps.FirstOrDefault(item => string.Equals(item.Id, executionStepId, StringComparison.Ordinal));
                if (step is not null) step.Status = stepStatus;
            }
        }

        if (!string.IsNullOrWhiteSpace(operationEvent.Message))
            StatusMessage = operationEvent.Message;
    }

    private async Task LoadOutputAsync(MaintenanceHistoryStepViewModel step, string? relativePath, string streamName)
    {
        ArgumentNullException.ThrowIfNull(step);
        OutputText = string.Empty;
        ErrorMessage = string.Empty;
        OutputTitle = $"{step.Name} · {streamName}";

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            ErrorMessage = $"该步骤没有可读取的 {streamName} 输出。";
            return;
        }

        try
        {
            var fullPath = ResolveOperationOutputPath(relativePath);
            if (!File.Exists(fullPath))
            {
                ErrorMessage = $"维护输出文件不存在：{relativePath}";
                return;
            }

            OutputText = await File.ReadAllTextAsync(fullPath, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            StatusMessage = "已取消窗口等待。";
        }
        catch (Exception exception)
        {
            ErrorMessage = $"读取维护输出失败：{exception.Message}";
        }
    }

    private string ResolveOperationOutputPath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            throw new InvalidDataException("仓储返回了绝对路径，已拒绝读取。");

        var segments = relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => string.Equals(segment, "..", StringComparison.Ordinal)))
            throw new InvalidDataException("仓储路径包含越界片段，已拒绝读取。");

        var root = Path.GetFullPath(_paths.OperationsDirectory);
        var normalizedRelativePath = relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(_paths.StorageRoot, normalizedRelativePath));
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("仓储路径越界，已拒绝读取。");

        return fullPath;
    }

    private void NotifyExecutionAvailability()
    {
        OnPropertyChanged(nameof(CanExecute));
        ((DelegateCommand)ExecuteCommand).RaiseCanExecuteChanged();
    }
}

/// <summary>不可编辑计划步骤；Executable 和 Arguments 仅由构造函数从 ExecutionPlan 快照填充。</summary>
public sealed class MaintenancePlanStepViewModel : ViewModelBase
{
    private MaintenanceStepStatus _status = MaintenanceStepStatus.Pending;

    public MaintenancePlanStepViewModel(ExecutionStep step)
    {
        Id = step.Id;
        Index = step.Index;
        Name = step.Name;
        Executable = step.Executable;
        Arguments = Array.AsReadOnly(step.Arguments.ToArray());
        IsReadOnly = step.IsReadOnly;
        CommandText = PosixCommandDisplay.Format(step.Executable, step.Arguments);
    }

    public string Id { get; }
    public int Index { get; }
    public string Name { get; }
    public string Executable { get; }
    public IReadOnlyList<string> Arguments { get; }
    public bool IsReadOnly { get; }
    public string CommandText { get; }

    public MaintenanceStepStatus Status
    {
        get => _status;
        internal set
        {
            if (!SetProperty(ref _status, value)) return;
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public string StatusText => MaintenanceStatusText.Step(Status);
}

public sealed class MaintenanceHistoryItemViewModel
{
    public MaintenanceHistoryItemViewModel(MaintenanceOperation operation)
    {
        OperationId = operation.Id;
        WorkflowId = operation.WorkflowId;
        ExtensionVersion = operation.ExtensionVersion;
        Status = operation.Status;
        StartedAt = operation.StartedAt;
        CompletedAt = operation.CompletedAt;
        OutcomeSummary = operation.OutcomeSummary ?? string.Empty;
        Steps = operation.Steps.OrderBy(step => step.Index).Select(step => new MaintenanceHistoryStepViewModel(step)).ToArray();
    }

    public string OperationId { get; }
    public string WorkflowId { get; }
    public string ExtensionVersion { get; }
    public MaintenanceOperationStatus Status { get; }
    public string StatusText => MaintenanceStatusText.Operation(Status);
    public DateTime StartedAt { get; }
    public DateTime? CompletedAt { get; }
    public string OutcomeSummary { get; }
    public IReadOnlyList<MaintenanceHistoryStepViewModel> Steps { get; }
}

public sealed class MaintenanceHistoryStepViewModel
{
    public MaintenanceHistoryStepViewModel(MaintenanceOperationStep step)
    {
        Id = step.Id;
        Name = step.Name;
        Status = step.Status;
        Executable = step.Executable;
        Arguments = Array.AsReadOnly(step.Arguments.ToArray());
        StdoutPath = step.StdoutPath;
        StderrPath = step.StderrPath;
        ExitCode = step.ExitCode;
        Duration = step.Duration;
        StartedAt = step.StartedAt;
        CompletedAt = step.CompletedAt;
        CommandText = PosixCommandDisplay.Format(step.Executable, step.Arguments);
    }

    public string Id { get; }
    public string Name { get; }
    public MaintenanceStepStatus Status { get; }
    public string StatusText => MaintenanceStatusText.Step(Status);
    public string Executable { get; }
    public IReadOnlyList<string> Arguments { get; }
    public string CommandText { get; }
    public string? StdoutPath { get; set; }
    public string? StderrPath { get; set; }
    public int? ExitCode { get; }
    public TimeSpan? Duration { get; }
    public DateTime? StartedAt { get; }
    public DateTime? CompletedAt { get; }
}

internal static class PosixCommandDisplay
{
    public static string Format(string executable, IReadOnlyList<string> arguments) =>
        string.Join(" ", new[] { Quote(executable) }.Concat(arguments.Select(Quote)));

    private static string Quote(string token) => $"'{token.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
}

internal static class MaintenanceStatusText
{
    public static string Operation(MaintenanceOperationStatus status) => status switch
    {
        MaintenanceOperationStatus.Planned => "待执行",
        MaintenanceOperationStatus.Running => "执行中",
        MaintenanceOperationStatus.StopRequested => "等待安全停止",
        MaintenanceOperationStatus.Succeeded => "成功",
        MaintenanceOperationStatus.Failed => "失败",
        MaintenanceOperationStatus.OutcomeUnknown => "结果未知",
        _ => status.ToString()
    };

    public static string Step(MaintenanceStepStatus status) => status switch
    {
        MaintenanceStepStatus.Pending => "待执行",
        MaintenanceStepStatus.Running => "执行中",
        MaintenanceStepStatus.Succeeded => "成功",
        MaintenanceStepStatus.Failed => "失败",
        MaintenanceStepStatus.Skipped => "已跳过",
        MaintenanceStepStatus.OutcomeUnknown => "结果未知",
        _ => status.ToString()
    };
}
