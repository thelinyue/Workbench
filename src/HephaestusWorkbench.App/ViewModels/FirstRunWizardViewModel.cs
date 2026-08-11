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
    private readonly Action _openPluginDirectory;
    private string _dataPath;
    private string _newMonitorPath = string.Empty;
    private string? _selectedMonitorPath;
    private string _errorMessage = string.Empty;
    private string _progressMessage = "等待开始初始化。";
    private int _currentStep;
    private bool _isBusy;
    private bool _initializationCompleted;

    public FirstRunWizardViewModel(
        string defaultDataPath,
        Func<string, IReadOnlyList<string>, IProgress<string>, Task> initializeAsync,
        Action browseDataPath,
        Action browseMonitorPath,
        Action openPluginDirectory)
    {
        _dataPath = Path.GetFullPath(defaultDataPath);
        MonitorPaths.Add(Path.Combine(_dataPath, "Inbox"));
        _initializeAsync = initializeAsync;
        _browseDataPath = browseDataPath;
        _browseMonitorPath = browseMonitorPath;
        _openPluginDirectory = openPluginDirectory;
        BackCommand = new DelegateCommand(() => CurrentStep--, () => CurrentStep > 0 && !IsBusy);
        NextCommand = new DelegateCommand(() => _ = NextAsync(), () => !IsBusy);
        AddMonitorCommand = new DelegateCommand(AddMonitorPath);
        RemoveMonitorCommand = new DelegateCommand(RemoveMonitorPath, () => !string.IsNullOrWhiteSpace(SelectedMonitorPath));
        BrowseDataPathCommand = new DelegateCommand(_browseDataPath);
        BrowseMonitorPathCommand = new DelegateCommand(_browseMonitorPath);
        OpenPluginDirectoryCommand = new DelegateCommand(_openPluginDirectory);
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
                OnPropertyChanged(nameof(MonitorPaths));
            }
            OnPropertyChanged(nameof(PluginDirectory));
        }
    }

    public string PluginDirectory => Path.Combine(DataPath, "Plugins");
    public string NewMonitorPath { get => _newMonitorPath; set => SetProperty(ref _newMonitorPath, value); }
    public string? SelectedMonitorPath
    {
        get => _selectedMonitorPath;
        set
        {
            if (SetProperty(ref _selectedMonitorPath, value)) (RemoveMonitorCommand as DelegateCommand)?.RaiseCanExecuteChanged();
        }
    }
    public string ErrorMessage { get => _errorMessage; private set => SetProperty(ref _errorMessage, value); }
    public string ProgressMessage { get => _progressMessage; private set => SetProperty(ref _progressMessage, value); }
    public int CurrentStep
    {
        get => _currentStep;
        set
        {
            if (!SetProperty(ref _currentStep, Math.Clamp(value, 0, 4))) return;
            OnPropertyChanged(nameof(StepText));
            OnPropertyChanged(nameof(NextButtonText));
            (BackCommand as DelegateCommand)?.RaiseCanExecuteChanged();
        }
    }
    public string StepText => $"步骤 {CurrentStep + 1} / 5";
    public string NextButtonText => CurrentStep == 0 ? "开始" : CurrentStep == 4 && _initializationCompleted ? "完成" : CurrentStep == 4 ? "开始初始化" : "下一步";
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) { (BackCommand as DelegateCommand)?.RaiseCanExecuteChanged(); (NextCommand as DelegateCommand)?.RaiseCanExecuteChanged(); } } }

    public ICommand BackCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand AddMonitorCommand { get; }
    public ICommand RemoveMonitorCommand { get; }
    public ICommand BrowseDataPathCommand { get; }
    public ICommand BrowseMonitorPathCommand { get; }
    public ICommand OpenPluginDirectoryCommand { get; }
    public event EventHandler? Finished;

    private void AddMonitorPath()
    {
        if (string.IsNullOrWhiteSpace(NewMonitorPath)) return;
        var normalized = Path.GetFullPath(NewMonitorPath.Trim());
        if (!MonitorPaths.Contains(normalized, StringComparer.OrdinalIgnoreCase)) MonitorPaths.Add(normalized);
        NewMonitorPath = string.Empty;
        ErrorMessage = string.Empty;
    }

    private void RemoveMonitorPath()
    {
        if (SelectedMonitorPath is null || MonitorPaths.Count <= 1) return;
        MonitorPaths.Remove(SelectedMonitorPath);
        SelectedMonitorPath = null;
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
        if (CurrentStep < 4)
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
