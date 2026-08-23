using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using HephaestusWorkbench.App;

namespace HephaestusWorkbench.App.ViewModels;

/// <summary>
/// 首次运行向导模型，负责收集路径并显示初始化进度。
/// 真正的目录、数据库和配置写入由初始化服务完成，避免 UI 自己拼装基础设施。
/// </summary>
public sealed class FirstRunWizardViewModel : ViewModelBase
{
    private readonly Func<string, IReadOnlyList<string>, IProgress<string>, Task> _initializeAsync;
    private readonly Action _browseDataPath;
    private readonly Action _browseMonitorPath;
    private string _dataPath;
    private string _newMonitorPath = string.Empty;
    private string? _selectedMonitorPath;
    private string _errorMessage = string.Empty;
    private string _directoryFeedback = string.Empty;
    private bool _directoryFeedbackIsError;
    private string _progressMessage = "等待开始初始化。";
    private int _currentStep;
    private bool _isBusy;
    private bool _initializationCompleted;

    public FirstRunWizardViewModel(
        string defaultDataPath,
        Func<string, IReadOnlyList<string>, IProgress<string>, Task> initializeAsync,
        Action browseDataPath,
        Action browseMonitorPath)
    {
        _dataPath = Path.GetFullPath(defaultDataPath);
        MonitorPaths.Add(Path.Combine(_dataPath, "Inbox"));
        _initializeAsync = initializeAsync;
        _browseDataPath = browseDataPath;
        _browseMonitorPath = browseMonitorPath;
        BackCommand = new DelegateCommand(() => CurrentStep--, () => CurrentStep > 0 && !IsBusy);
        NextCommand = new DelegateCommand(() => _ = NextAsync(), () => !IsBusy);
        AddMonitorCommand = new DelegateCommand(AddMonitorPath);
        RemoveMonitorCommand = new DelegateCommand(RemoveMonitorPath, CanRemoveMonitorPath);
        BrowseDataPathCommand = new DelegateCommand(_browseDataPath);
        BrowseMonitorPathCommand = new DelegateCommand(_browseMonitorPath);
        SelectedMonitorPath = MonitorPaths[0];
    }

    public ObservableCollection<string> MonitorPaths { get; } = new();
    public string DataPath
    {
        get => _dataPath;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? string.Empty : Path.GetFullPath(value.Trim());
            var previousDefault = string.IsNullOrWhiteSpace(_dataPath) ? string.Empty : Path.Combine(_dataPath, "Inbox");
            if (!SetProperty(ref _dataPath, normalized)) return;
            if (MonitorPaths.Count == 1 && string.Equals(MonitorPaths[0], previousDefault, StringComparison.OrdinalIgnoreCase))
            {
                MonitorPaths[0] = Path.Combine(normalized, "Inbox");
                if (string.Equals(SelectedMonitorPath, previousDefault, StringComparison.OrdinalIgnoreCase))
                    SelectedMonitorPath = MonitorPaths[0];
                OnPropertyChanged(nameof(MonitorPaths));
            }
        }
    }

    public string NewMonitorPath
    {
        get => _newMonitorPath;
        set
        {
            if (SetProperty(ref _newMonitorPath, value)) ClearDirectoryFeedback();
        }
    }
    public string? SelectedMonitorPath
    {
        get => _selectedMonitorPath;
        set
        {
            if (SetProperty(ref _selectedMonitorPath, value))
            {
                (RemoveMonitorCommand as DelegateCommand)?.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(RemoveMonitorHint));
            }
        }
    }
    public string ErrorMessage { get => _errorMessage; private set => SetProperty(ref _errorMessage, value); }
    public string DirectoryFeedback { get => _directoryFeedback; private set => SetProperty(ref _directoryFeedback, value); }
    public bool DirectoryFeedbackIsError { get => _directoryFeedbackIsError; private set => SetProperty(ref _directoryFeedbackIsError, value); }
    public string MonitorPathCountText => $"已添加 {MonitorPaths.Count} 个目录";
    public string RemoveMonitorHint => MonitorPaths.Count <= 1
        ? "至少保留一个目录"
        : string.IsNullOrWhiteSpace(SelectedMonitorPath) ? "请选择一个目录" : "移除所选目录";
    public string ProgressMessage { get => _progressMessage; private set => SetProperty(ref _progressMessage, value); }
    public int CurrentStep
    {
        get => _currentStep;
        set
        {
            if (!SetProperty(ref _currentStep, Math.Clamp(value, 0, 3))) return;
            OnPropertyChanged(nameof(StepText));
            OnPropertyChanged(nameof(NextButtonText));
            (BackCommand as DelegateCommand)?.RaiseCanExecuteChanged();
        }
    }
    public string StepText => $"步骤 {CurrentStep + 1} / 4";
    public string NextButtonText => CurrentStep == 0 ? "开始" : CurrentStep == 3 && _initializationCompleted ? "完成" : CurrentStep == 3 ? "开始初始化" : "下一步";
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) { (BackCommand as DelegateCommand)?.RaiseCanExecuteChanged(); (NextCommand as DelegateCommand)?.RaiseCanExecuteChanged(); } } }

    public ICommand BackCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand AddMonitorCommand { get; }
    public ICommand RemoveMonitorCommand { get; }
    public ICommand BrowseDataPathCommand { get; }
    public ICommand BrowseMonitorPathCommand { get; }
    public event EventHandler? Finished;

    private void AddMonitorPath()
    {
        if (string.IsNullOrWhiteSpace(NewMonitorPath))
        {
            SetDirectoryFeedback("请输入要监控的目录路径。", isError: true);
            return;
        }

        string normalized;
        try { normalized = Path.GetFullPath(NewMonitorPath.Trim()); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            SetDirectoryFeedback("目录路径无效，请检查路径后重试。", isError: true);
            return;
        }

        if (MonitorPaths.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            SetDirectoryFeedback("该目录已经添加，无需重复添加。", isError: true);
            return;
        }

        MonitorPaths.Add(normalized);
        SelectedMonitorPath = normalized;
        NewMonitorPath = string.Empty;
        SetDirectoryFeedback($"已添加目录（共 {MonitorPaths.Count} 个）。", isError: false);
        OnPropertyChanged(nameof(MonitorPathCountText));
        OnPropertyChanged(nameof(RemoveMonitorHint));
        (RemoveMonitorCommand as DelegateCommand)?.RaiseCanExecuteChanged();
    }

    private void RemoveMonitorPath()
    {
        if (!CanRemoveMonitorPath())
        {
            SetDirectoryFeedback(RemoveMonitorHint, isError: true);
            return;
        }

        var removedPath = SelectedMonitorPath!;
        MonitorPaths.Remove(removedPath);
        SelectedMonitorPath = MonitorPaths.FirstOrDefault();
        SetDirectoryFeedback($"已移除目录（剩余 {MonitorPaths.Count} 个）。", isError: false);
        OnPropertyChanged(nameof(MonitorPathCountText));
        OnPropertyChanged(nameof(RemoveMonitorHint));
        (RemoveMonitorCommand as DelegateCommand)?.RaiseCanExecuteChanged();
    }

    private bool CanRemoveMonitorPath()
        => !string.IsNullOrWhiteSpace(SelectedMonitorPath) && MonitorPaths.Count > 1;

    private void SetDirectoryFeedback(string message, bool isError)
    {
        DirectoryFeedbackIsError = isError;
        DirectoryFeedback = message;
    }

    private void ClearDirectoryFeedback()
    {
        if (string.IsNullOrEmpty(DirectoryFeedback)) return;
        DirectoryFeedback = string.Empty;
        DirectoryFeedbackIsError = false;
    }

    private async Task NextAsync()
    {
        ErrorMessage = string.Empty;
        if (CurrentStep == 1 && string.IsNullOrWhiteSpace(DataPath))
        {
            ErrorMessage = "数据目录不能为空。";
            return;
        }
        if (CurrentStep == 2 && MonitorPaths.Count == 0)
        {
            ErrorMessage = "至少需要一个日志监控目录。";
            return;
        }
        if (CurrentStep < 3)
        {
            CurrentStep++;
            return;
        }
        if (_initializationCompleted)
        {
            Finished?.Invoke(this, EventArgs.Empty);
            return;
        }

        IsBusy = true;
        try
        {
            var progress = new Progress<string>(message => ProgressMessage = message);
            await _initializeAsync(DataPath, MonitorPaths.ToArray(), progress);
            _initializationCompleted = true;
            ProgressMessage = "初始化完成，可以点击“完成”进入工作台。";
            OnPropertyChanged(nameof(NextButtonText));
        }
        catch (Exception ex)
        {
            ErrorMessage = $"初始化失败：{ex.Message}";
            ProgressMessage = "请修正目录或权限后重试。";
        }
        finally { IsBusy = false; }
    }
}
