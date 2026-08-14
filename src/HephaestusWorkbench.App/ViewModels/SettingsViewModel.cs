using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.App.ViewModels;

/// <summary>设置页模型，管理日志监控目录和基础界面偏好。</summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly LogInboxService _inbox;
    private string _newWatchDirectory = string.Empty;
    private string? _selectedWatchDirectory;
    private string _message = string.Empty;
    private string _directoryFeedback = string.Empty;
    private bool _directoryFeedbackIsError;
    private readonly Func<int> _getOpenReportCount;
    private readonly Func<string, string?> _applyTheme;
    private bool _isLoading;
    private bool _hasUnsavedChanges;
    private int _maxOpenReports = 10;
    private string _selectedTheme = "Light";

    public SettingsViewModel(SettingsService settings, LogInboxService inbox, Func<int> getOpenReportCount, Func<string, string?> applyTheme)
    {
        _settings = settings;
        _inbox = inbox;
        _getOpenReportCount = getOpenReportCount;
        _applyTheme = applyTheme;
        SaveCommand = new DelegateCommand(() => _ = SaveAsync());
        AddWatchDirectoryCommand = new DelegateCommand(AddWatchDirectory);
        RemoveWatchDirectoryCommand = new DelegateCommand(RemoveWatchDirectory, CanRemoveWatchDirectory);
        _ = LoadAsync();
    }

    public ObservableCollection<string> WatchDirectories { get; } = new();
    public IReadOnlyList<ThemeOption> ThemeOptions { get; } = new[]
    {
        new ThemeOption("Light", "亮色"),
        new ThemeOption("Dark", "深色")
    };
    public string NewWatchDirectory
    {
        get => _newWatchDirectory;
        set
        {
            if (SetProperty(ref _newWatchDirectory, value)) ClearDirectoryFeedback();
        }
    }
    public string? SelectedWatchDirectory
    {
        get => _selectedWatchDirectory;
        set
        {
            if (SetProperty(ref _selectedWatchDirectory, value))
            {
                (RemoveWatchDirectoryCommand as DelegateCommand)?.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(RemoveWatchDirectoryHint));
            }
        }
    }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public bool HasUnsavedChanges { get => _hasUnsavedChanges; private set => SetProperty(ref _hasUnsavedChanges, value); }
    public string DirectoryFeedback { get => _directoryFeedback; private set => SetProperty(ref _directoryFeedback, value); }
    public bool DirectoryFeedbackIsError { get => _directoryFeedbackIsError; private set => SetProperty(ref _directoryFeedbackIsError, value); }
    public string WatchDirectoryCountText => $"已添加 {WatchDirectories.Count} 个目录";
    public string RemoveWatchDirectoryHint => WatchDirectories.Count <= 1
        ? "至少保留一个目录"
        : string.IsNullOrWhiteSpace(SelectedWatchDirectory) ? "请选择一个目录" : "移除所选目录";
    public ICommand SaveCommand { get; }
    public ICommand AddWatchDirectoryCommand { get; }
    public ICommand RemoveWatchDirectoryCommand { get; }
    public int MaxOpenReports
    {
        get => _maxOpenReports;
        set
        {
            if (SetProperty(ref _maxOpenReports, value)) MarkUnsaved();
        }
    }
    public string SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (SetProperty(ref _selectedTheme, value)) MarkUnsaved();
        }
    }

    public void AddWatchDirectory()
    {
        if (string.IsNullOrWhiteSpace(NewWatchDirectory))
        {
            SetDirectoryFeedback("请输入要监控的目录路径。", isError: true);
            return;
        }

        string normalized;
        try { normalized = Path.GetFullPath(NewWatchDirectory.Trim()); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            SetDirectoryFeedback("目录路径无效，请检查路径后重试。", isError: true);
            return;
        }

        if (WatchDirectories.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            SetDirectoryFeedback("该目录已经添加，无需重复添加。", isError: true);
            return;
        }

        WatchDirectories.Add(normalized);
        MarkUnsaved();
        SelectedWatchDirectory = normalized;
        NewWatchDirectory = string.Empty;
        SetDirectoryFeedback($"已添加目录（共 {WatchDirectories.Count} 个）。", isError: false);
        OnPropertyChanged(nameof(WatchDirectoryCountText));
        OnPropertyChanged(nameof(RemoveWatchDirectoryHint));
        (RemoveWatchDirectoryCommand as DelegateCommand)?.RaiseCanExecuteChanged();
    }

    public void RemoveWatchDirectory()
    {
        if (!CanRemoveWatchDirectory())
        {
            SetDirectoryFeedback(RemoveWatchDirectoryHint, isError: true);
            return;
        }

        WatchDirectories.Remove(SelectedWatchDirectory!);
        MarkUnsaved();
        SelectedWatchDirectory = WatchDirectories.FirstOrDefault();
        SetDirectoryFeedback($"已移除目录（剩余 {WatchDirectories.Count} 个）。", isError: false);
        OnPropertyChanged(nameof(WatchDirectoryCountText));
        OnPropertyChanged(nameof(RemoveWatchDirectoryHint));
        (RemoveWatchDirectoryCommand as DelegateCommand)?.RaiseCanExecuteChanged();
    }

    private bool CanRemoveWatchDirectory()
        => !string.IsNullOrWhiteSpace(SelectedWatchDirectory) && WatchDirectories.Count > 1;

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

    private async Task LoadAsync()
    {
        _isLoading = true;
        try
        {
            WatchDirectories.Clear();
            foreach (var directory in await _settings.GetWatchDirectoriesAsync()) WatchDirectories.Add(directory);
            SelectedWatchDirectory = WatchDirectories.FirstOrDefault();
            OnPropertyChanged(nameof(WatchDirectoryCountText));
            OnPropertyChanged(nameof(RemoveWatchDirectoryHint));
            (RemoveWatchDirectoryCommand as DelegateCommand)?.RaiseCanExecuteChanged();
            MaxOpenReports = await _settings.GetReportMaxTabsAsync();
            SelectedTheme = await _settings.GetThemeAsync();
            HasUnsavedChanges = false;
        }
        catch (Exception ex) { Message = $"读取设置失败：{ex.Message}"; }
        finally { _isLoading = false; }
    }

    private async Task SaveAsync()
    {
        try
        {
            if (MaxOpenReports is < 1 or > 10)
            {
                Message = "最大打开报告数量必须在 1 到 10 之间。";
                return;
            }
            if (WatchDirectories.Count == 0)
            {
                Message = "至少需要一个日志监控目录。";
                return;
            }
            if (MaxOpenReports < _getOpenReportCount())
            {
                Message = $"当前已打开 {_getOpenReportCount()} 个报告，请先关闭多余报告。";
                return;
            }
            await _inbox.SetWatchDirectoriesAsync(WatchDirectories);
            await _settings.SetReportMaxTabsAsync(MaxOpenReports);
            await _settings.SetThemeAsync(SelectedTheme);
            if (_applyTheme(SelectedTheme) is { } themeError)
            {
                Message = $"主题切换失败：{themeError}";
                return;
            }
            HasUnsavedChanges = false;
            Message = "设置已保存。";
        }
        catch (Exception ex) { Message = $"保存失败：{ex.Message}"; }
    }

    private void MarkUnsaved()
    {
        if (!_isLoading) HasUnsavedChanges = true;
    }
}

/// <summary>设置页展示的主题选项，值用于配置持久化，名称用于中文界面显示。</summary>
public sealed record ThemeOption(string Value, string DisplayName);
