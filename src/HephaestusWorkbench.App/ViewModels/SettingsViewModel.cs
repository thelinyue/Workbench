using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Data;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.App.ViewModels;

/// <summary>
/// 设置页模型，管理日志监控目录和基础界面偏好。
/// 目录在界面中使用带状态的展示项，但保存时仍转换为原有的字符串路径集合，避免改变配置接口。
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly LogInboxService _inbox;
    private readonly Func<string, string?> _applyTheme;
    private readonly Action<SshTerminalPreferences>? _sshPreferencesSaved;
    private readonly DataPaths? _paths;
    private readonly BootstrapConfigurationStore? _bootstrapStore;
    private readonly DirectoryOpenService? _directoryOpen;
    private readonly Func<string?>? _startReplacementProcess;
    private readonly Action? _shutdownCurrentProcess;
    private readonly List<string> _savedWatchDirectories = new();
    private string _newWatchDirectory = string.Empty;
    private WatchDirectoryItemViewModel? _selectedWatchDirectory;
    private string _message = string.Empty;
    private bool _messageIsError;
    private string _directoryFeedback = string.Empty;
    private bool _directoryFeedbackIsError;
    private bool _isLoading;
    private bool _hasUnsavedChanges;
    private string _selectedTheme = AppSettingsConfig.LightTheme;
    private string _persistedTheme = AppSettingsConfig.LightTheme;
    private string? _themePreviewError;
    private int _sshDefaultPort = 22;
    private string _terminalFontFamily = "Cascadia Mono";
    private double _terminalFontSize = 14;
    private bool _automaticSshReconnect = true;
    private bool _autoCheckExtensionUpdates = true;
    private string _candidateDataRoot = string.Empty;
    private string _storageFeedback = string.Empty;
    private bool _storageFeedbackIsError;
    private bool _dataRootChangeRegistered;

    public SettingsViewModel(
        SettingsService settings,
        LogInboxService inbox,
        Func<string, string?> applyTheme,
        Action<SshTerminalPreferences>? sshPreferencesSaved = null)
        : this(settings, inbox, applyTheme, sshPreferencesSaved, null, null, null, null, null)
    {
    }

    /// <summary>
    /// 创建正式设置页模型。数据根切换只更新下一次启动读取的 bootstrap 指针，
    /// 当前进程继续使用既有 <see cref="DataPaths"/>，不会迁移、复制或删除工作区数据。
    /// </summary>
    public SettingsViewModel(
        SettingsService settings,
        LogInboxService inbox,
        Func<string, string?> applyTheme,
        Action<SshTerminalPreferences>? sshPreferencesSaved,
        DataPaths? paths,
        BootstrapConfigurationStore? bootstrapStore,
        DirectoryOpenService? directoryOpen,
        Func<string?>? startReplacementProcess,
        Action? shutdownCurrentProcess)
    {
        _settings = settings;
        _inbox = inbox;
        _applyTheme = applyTheme;
        _sshPreferencesSaved = sshPreferencesSaved;
        _paths = paths;
        _bootstrapStore = bootstrapStore;
        _directoryOpen = directoryOpen;
        _startReplacementProcess = startReplacementProcess;
        _shutdownCurrentProcess = shutdownCurrentProcess;
        SaveCommand = new DelegateCommand(() => _ = SaveAsync(), CanSave);
        AddWatchDirectoryCommand = new DelegateCommand(AddWatchDirectory);
        RemoveWatchDirectoryCommand = new DelegateCommand(RemoveWatchDirectory, CanRemoveWatchDirectory);
        OpenWorkspaceDirectoryCommand = new DelegateCommand(OpenWorkspaceDirectory, () => _paths is not null && _directoryOpen is not null);
        RegisterDataRootChangeCommand = new DelegateCommand(() => _ = RegisterDataRootChangeAsync(), () => _bootstrapStore is not null);
        RestartApplicationCommand = new DelegateCommand(RestartApplication, () => _dataRootChangeRegistered);
        Initialization = LoadAsync();
    }

    /// <summary>设置初始化完成任务；主窗口显示和测试交互前必须等待，避免加载过程覆盖用户修改。</summary>
    public Task Initialization { get; }

    public ObservableCollection<WatchDirectoryItemViewModel> WatchDirectories { get; } = new();

    public IReadOnlyList<ThemeOption> ThemeOptions { get; } = new[]
    {
        new ThemeOption(AppSettingsConfig.LightTheme, "亮色"),
        new ThemeOption(AppSettingsConfig.DarkTheme, "深色")
    };

    public string NewWatchDirectory
    {
        get => _newWatchDirectory;
        set
        {
            if (SetProperty(ref _newWatchDirectory, value)) ClearDirectoryFeedback();
        }
    }

    public WatchDirectoryItemViewModel? SelectedWatchDirectory
    {
        get => _selectedWatchDirectory;
        set
        {
            if (!SetProperty(ref _selectedWatchDirectory, value)) return;
            (RemoveWatchDirectoryCommand as DelegateCommand)?.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(RemoveWatchDirectoryHint));
        }
    }

    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    public bool MessageIsError
    {
        get => _messageIsError;
        private set => SetProperty(ref _messageIsError, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (!SetProperty(ref _isLoading, value)) return;
            (SaveCommand as DelegateCommand)?.RaiseCanExecuteChanged();
        }
    }

    public bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        private set
        {
            if (!SetProperty(ref _hasUnsavedChanges, value)) return;
            OnPropertyChanged(nameof(WatchDirectoryCountText));
            (SaveCommand as DelegateCommand)?.RaiseCanExecuteChanged();
        }
    }

    public string DirectoryFeedback
    {
        get => _directoryFeedback;
        private set
        {
            if (!SetProperty(ref _directoryFeedback, value)) return;
            OnPropertyChanged(nameof(HasDirectoryFeedback));
        }
    }

    public bool DirectoryFeedbackIsError
    {
        get => _directoryFeedbackIsError;
        private set => SetProperty(ref _directoryFeedbackIsError, value);
    }

    public bool HasDirectoryFeedback => !string.IsNullOrWhiteSpace(DirectoryFeedback);
    public bool HasWatchDirectories => WatchDirectories.Count > 0;
    public bool HasUnsavedWatchDirectoryChanges => !CurrentWatchDirectoriesEqualSaved();

    public string WatchDirectoryCountText
        => HasUnsavedWatchDirectoryChanges
            ? $"已添加 {WatchDirectories.Count} 个目录 · 待保存"
            : $"已添加 {WatchDirectories.Count} 个目录";

    public string RemoveWatchDirectoryHint => WatchDirectories.Count <= 1
        ? "至少保留一个目录"
        : SelectedWatchDirectory is null ? "请选择一个目录" : "移除所选目录";

    public ICommand SaveCommand { get; }
    public ICommand AddWatchDirectoryCommand { get; }
    public ICommand RemoveWatchDirectoryCommand { get; }
    public ICommand OpenWorkspaceDirectoryCommand { get; }
    public ICommand RegisterDataRootChangeCommand { get; }
    public ICommand RestartApplicationCommand { get; }

    public string CurrentDataRoot => _paths?.Root ?? string.Empty;

    public string CandidateDataRoot
    {
        get => _candidateDataRoot;
        set
        {
            if (!SetProperty(ref _candidateDataRoot, value)) return;
            _dataRootChangeRegistered = false;
            (RestartApplicationCommand as DelegateCommand)?.RaiseCanExecuteChanged();
            SetStorageFeedback(string.Empty, isError: false);
        }
    }

    public string StorageFeedback
    {
        get => _storageFeedback;
        private set
        {
            if (!SetProperty(ref _storageFeedback, value)) return;
            OnPropertyChanged(nameof(HasStorageFeedback));
        }
    }

    public bool StorageFeedbackIsError
    {
        get => _storageFeedbackIsError;
        private set => SetProperty(ref _storageFeedbackIsError, value);
    }

    public bool HasStorageFeedback => !string.IsNullOrWhiteSpace(StorageFeedback);

    public int SshDefaultPort
    {
        get => _sshDefaultPort;
        set { if (SetProperty(ref _sshDefaultPort, value)) MarkUnsaved(); }
    }

    public string TerminalFontFamily
    {
        get => _terminalFontFamily;
        set { if (SetProperty(ref _terminalFontFamily, value)) MarkUnsaved(); }
    }

    public double TerminalFontSize
    {
        get => _terminalFontSize;
        set { if (SetProperty(ref _terminalFontSize, value)) MarkUnsaved(); }
    }

    public bool AutomaticSshReconnect
    {
        get => _automaticSshReconnect;
        set { if (SetProperty(ref _automaticSshReconnect, value)) MarkUnsaved(); }
    }

    public bool AutoCheckExtensionUpdates
    {
        get => _autoCheckExtensionUpdates;
        set { if (SetProperty(ref _autoCheckExtensionUpdates, value)) MarkUnsaved(); }
    }

    public string SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (!SetProperty(ref _selectedTheme, value)) return;
            MarkUnsaved();
            ApplyThemePreview();
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

        if (WatchDirectories.Any(item => string.Equals(item.Path, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            SetDirectoryFeedback("该目录已经添加，无需重复添加。", isError: true);
            return;
        }

        var item = new WatchDirectoryItemViewModel(normalized);
        WatchDirectories.Add(item);
        MarkUnsaved();
        SelectedWatchDirectory = item;
        NewWatchDirectory = string.Empty;
        SetDirectoryFeedback($"已添加目录（共 {WatchDirectories.Count} 个）。", isError: false);
        NotifyDirectoryCollectionChanged();
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
        NotifyDirectoryCollectionChanged();
    }

    private bool CanRemoveWatchDirectory()
        => SelectedWatchDirectory is not null && WatchDirectories.Count > 1;

    private bool CanSave() => HasUnsavedChanges && !IsLoading;

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

    private void SetMessage(string message, bool isError)
    {
        MessageIsError = isError;
        Message = message;
    }

    private void SetStorageFeedback(string message, bool isError)
    {
        StorageFeedbackIsError = isError;
        StorageFeedback = message;
    }

    private void OpenWorkspaceDirectory()
    {
        if (_paths is null || _directoryOpen is null) return;
        var result = _directoryOpen.OpenWorkspaceDirectory(_paths.Root);
        SetStorageFeedback(
            result.Succeeded ? "已打开当前工作空间目录。" : $"无法打开工作空间目录：{result.ErrorMessage}",
            isError: !result.Succeeded);
    }

    private async Task RegisterDataRootChangeAsync()
    {
        if (_bootstrapStore is null) return;

        _dataRootChangeRegistered = false;
        (RestartApplicationCommand as DelegateCommand)?.RaiseCanExecuteChanged();
        try
        {
            if (string.IsNullOrWhiteSpace(CandidateDataRoot))
            {
                SetStorageFeedback("请选择一个空目录作为新的数据目录。", isError: true);
                return;
            }

            var candidate = Path.GetFullPath(CandidateDataRoot.Trim());
            if (!Directory.Exists(candidate))
            {
                SetStorageFeedback($"所选数据目录不存在或无法访问：{candidate}", isError: true);
                return;
            }

            if (Directory.EnumerateFileSystemEntries(candidate).Any())
            {
                SetStorageFeedback($"所选数据目录必须为空：{candidate}", isError: true);
                return;
            }

            await _bootstrapStore.WriteAsync(candidate);
            CandidateDataRoot = candidate;
            _dataRootChangeRegistered = true;
            (RestartApplicationCommand as DelegateCommand)?.RaiseCanExecuteChanged();
            SetStorageFeedback($"新的数据目录已登记，将在重启后生效：{candidate}", isError: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            SetStorageFeedback($"数据目录登记失败：{ex.Message}", isError: true);
        }
    }

    private void RestartApplication()
    {
        if (!_dataRootChangeRegistered || _startReplacementProcess is null || _shutdownCurrentProcess is null) return;
        try
        {
            var error = _startReplacementProcess();
            if (!string.IsNullOrWhiteSpace(error))
            {
                SetStorageFeedback($"重新启动失败：{error}", isError: true);
                return;
            }

            _shutdownCurrentProcess();
        }
        catch (Exception ex)
        {
            SetStorageFeedback($"重新启动失败：{ex.Message}", isError: true);
        }
    }

    private async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            WatchDirectories.Clear();
            foreach (var directory in await _settings.GetWatchDirectoriesAsync())
                WatchDirectories.Add(new WatchDirectoryItemViewModel(directory));

            _savedWatchDirectories.Clear();
            _savedWatchDirectories.AddRange(CurrentWatchDirectories());
            SelectedWatchDirectory = WatchDirectories.FirstOrDefault();
            NotifyDirectoryCollectionChanged();

            SelectedTheme = await _settings.GetThemeAsync();
            _persistedTheme = SelectedTheme;
            var ssh = await _settings.GetSshTerminalPreferencesAsync();
            SshDefaultPort = ssh.DefaultPort;
            TerminalFontFamily = ssh.FontFamily;
            TerminalFontSize = ssh.FontSize;
            AutomaticSshReconnect = ssh.ReconnectBehavior == SshReconnectBehavior.AutomaticThreeAttempts;
            AutoCheckExtensionUpdates = await _settings.GetExtensionAutoCheckUpdatesAsync();
            _themePreviewError = null;
            HasUnsavedChanges = false;
        }
        catch (Exception ex)
        {
            SetMessage($"读取设置失败：{ex.Message}", isError: true);
        }
        finally
        {
            IsLoading = false;
            (SaveCommand as DelegateCommand)?.RaiseCanExecuteChanged();
        }
    }

    private async Task SaveAsync()
    {
        if (!HasUnsavedChanges) return;

        var themeChanged = !string.Equals(SelectedTheme, _persistedTheme, StringComparison.OrdinalIgnoreCase);
        try
        {
            if (WatchDirectories.Count == 0)
            {
                SetMessage("至少需要一个日志监控目录。", isError: true);
                return;
            }
            if (_themePreviewError is not null)
            {
                SetMessage($"主题预览失败，未保存主题：{_themePreviewError}", isError: true);
                return;
            }

            await _inbox.SetWatchDirectoriesAsync(CurrentWatchDirectories());
            await _settings.SetThemeAsync(SelectedTheme);
            var reconnectBehavior = AutomaticSshReconnect
                ? SshReconnectBehavior.AutomaticThreeAttempts
                : SshReconnectBehavior.Disabled;
            await _settings.SetSshTerminalPreferencesAsync(
                SshDefaultPort,
                TerminalFontFamily,
                TerminalFontSize,
                reconnectBehavior);
            await _settings.SetExtensionAutoCheckUpdatesAsync(AutoCheckExtensionUpdates);
            _sshPreferencesSaved?.Invoke(new SshTerminalPreferences(
                SshDefaultPort,
                TerminalFontFamily.Trim(),
                TerminalFontSize,
                reconnectBehavior));

            foreach (var directory in WatchDirectories) directory.RefreshAvailability();
            _savedWatchDirectories.Clear();
            _savedWatchDirectories.AddRange(CurrentWatchDirectories());
            _persistedTheme = SelectedTheme;
            HasUnsavedChanges = false;
            SetMessage("设置已保存。", isError: false);
        }
        catch (Exception ex)
        {
            var prefix = themeChanged && _themePreviewError is null
                ? "设置保存失败：主题预览已应用但尚未保存。"
                : "保存失败：";
            SetMessage($"{prefix}{ex.Message}", isError: true);
        }
    }

    private void ApplyThemePreview()
    {
        if (_isLoading) return;

        try
        {
            _themePreviewError = _applyTheme(SelectedTheme);
        }
        catch (Exception ex)
        {
            _themePreviewError = ex.Message;
        }

        if (_themePreviewError is null)
            SetMessage("主题预览已应用，点击保存设置后持久化。", isError: false);
        else
            SetMessage($"主题预览失败：{_themePreviewError}", isError: true);
    }

    private void MarkUnsaved()
    {
        if (!_isLoading) HasUnsavedChanges = true;
    }

    private IReadOnlyList<string> CurrentWatchDirectories()
        => WatchDirectories.Select(item => item.Path).ToArray();

    private bool CurrentWatchDirectoriesEqualSaved()
        => CurrentWatchDirectories().SequenceEqual(_savedWatchDirectories, StringComparer.OrdinalIgnoreCase);

    private void NotifyDirectoryCollectionChanged()
    {
        OnPropertyChanged(nameof(HasWatchDirectories));
        OnPropertyChanged(nameof(HasUnsavedWatchDirectoryChanges));
        OnPropertyChanged(nameof(WatchDirectoryCountText));
        OnPropertyChanged(nameof(RemoveWatchDirectoryHint));
        (RemoveWatchDirectoryCommand as DelegateCommand)?.RaiseCanExecuteChanged();
    }
}

/// <summary>
/// 设置页中的目录展示项。
/// 目录状态只用于帮助用户判断配置是否可用，不参与配置持久化。
/// </summary>
public sealed class WatchDirectoryItemViewModel : ViewModelBase
{
    private bool _isAccessible;

    public WatchDirectoryItemViewModel(string path)
    {
        Path = path;
        RefreshAvailability();
    }

    public string Path { get; }
    public bool IsAccessible
    {
        get => _isAccessible;
        private set => SetProperty(ref _isAccessible, value);
    }

    public string AvailabilityText => IsAccessible ? "可访问" : "路径不存在或无法访问";

    public void RefreshAvailability()
    {
        var isAccessible = Directory.Exists(Path);
        if (!SetProperty(ref _isAccessible, isAccessible)) return;
        OnPropertyChanged(nameof(AvailabilityText));
    }
}

/// <summary>设置页展示的主题选项，值用于配置持久化，名称用于中文界面显示。</summary>
public sealed record ThemeOption(string Value, string DisplayName);
