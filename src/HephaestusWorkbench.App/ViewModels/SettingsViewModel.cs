using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.App.ViewModels;

/// <summary>设置页模型，管理多个日志监控目录和报告查看偏好。</summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly LogInboxService _inbox;
    private string _newWatchDirectory = string.Empty;
    private string? _selectedWatchDirectory;
    private string _message = string.Empty;
    private readonly Func<int> _getOpenReportCount;
    private readonly Func<string, string?> _applyTheme;
    private bool _reportRestoreEnabled = true;
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
        RemoveWatchDirectoryCommand = new DelegateCommand(RemoveWatchDirectory, () => !string.IsNullOrWhiteSpace(SelectedWatchDirectory) && WatchDirectories.Count > 1);
        _ = LoadAsync();
    }

    public ObservableCollection<string> WatchDirectories { get; } = new();
    public IReadOnlyList<ThemeOption> ThemeOptions { get; } = new[]
    {
        new ThemeOption("Light", "亮色"),
        new ThemeOption("Dark", "深色")
    };
    public string NewWatchDirectory { get => _newWatchDirectory; set => SetProperty(ref _newWatchDirectory, value); }
    public string? SelectedWatchDirectory
    {
        get => _selectedWatchDirectory;
        set
        {
            if (SetProperty(ref _selectedWatchDirectory, value)) (RemoveWatchDirectoryCommand as DelegateCommand)?.RaiseCanExecuteChanged();
        }
    }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public ICommand SaveCommand { get; }
    public ICommand AddWatchDirectoryCommand { get; }
    public ICommand RemoveWatchDirectoryCommand { get; }
    public bool ReportRestoreEnabled { get => _reportRestoreEnabled; set => SetProperty(ref _reportRestoreEnabled, value); }
    public int MaxOpenReports { get => _maxOpenReports; set => SetProperty(ref _maxOpenReports, value); }
    public string SelectedTheme { get => _selectedTheme; set => SetProperty(ref _selectedTheme, value); }

    public void AddWatchDirectory()
    {
        if (string.IsNullOrWhiteSpace(NewWatchDirectory)) return;
        var normalized = Path.GetFullPath(NewWatchDirectory.Trim());
        if (!WatchDirectories.Contains(normalized, StringComparer.OrdinalIgnoreCase)) WatchDirectories.Add(normalized);
        NewWatchDirectory = string.Empty;
        (RemoveWatchDirectoryCommand as DelegateCommand)?.RaiseCanExecuteChanged();
    }

    public void RemoveWatchDirectory()
    {
        if (SelectedWatchDirectory is null || WatchDirectories.Count <= 1) return;
        WatchDirectories.Remove(SelectedWatchDirectory);
        SelectedWatchDirectory = null;
        (RemoveWatchDirectoryCommand as DelegateCommand)?.RaiseCanExecuteChanged();
    }

    private async Task LoadAsync()
    {
        try
        {
            WatchDirectories.Clear();
            foreach (var directory in await _settings.GetWatchDirectoriesAsync()) WatchDirectories.Add(directory);
            ReportRestoreEnabled = await _settings.GetReportRestoreEnabledAsync();
            MaxOpenReports = await _settings.GetReportMaxTabsAsync();
            SelectedTheme = await _settings.GetThemeAsync();
        }
        catch (Exception ex) { Message = $"读取设置失败：{ex.Message}"; }
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
            await _settings.SetReportRestoreEnabledAsync(ReportRestoreEnabled);
            await _settings.SetReportMaxTabsAsync(MaxOpenReports);
            await _settings.SetThemeAsync(SelectedTheme);
            if (_applyTheme(SelectedTheme) is { } themeError)
            {
                Message = $"主题切换失败：{themeError}";
                return;
            }
            Message = "设置已保存。";
        }
        catch (Exception ex) { Message = $"保存失败：{ex.Message}"; }
    }
}

/// <summary>设置页展示的主题选项，值用于配置持久化，名称用于中文界面显示。</summary>
public sealed record ThemeOption(string Value, string DisplayName);
