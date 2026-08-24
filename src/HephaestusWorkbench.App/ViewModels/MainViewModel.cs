using HephaestusWorkbench.PluginSDK;
using HephaestusWorkbench.Data;
using HephaestusWorkbench.Services;
using System.Windows.Input;
using Wpf = System.Windows;

namespace HephaestusWorkbench.App.ViewModels;

/// <summary>工作台 Shell 模型，负责顶层导航和全局运行状态，不承载页面业务。</summary>
public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private readonly LogInboxService _inbox;
    private readonly DirectoryOpenService _directoryOpen;
    private readonly WorkbenchLogger _logger;
    private readonly SettingsService _settings;
    private readonly CancellationTokenSource _extensionRefreshCancellation = new();
    private Task? _extensionRefreshTask;
    private bool _disposed;
    private object? _currentPage;
    private NavigationItem? _selectedNavigationItem;
    private string _globalWarningText = string.Empty;
    private bool _isSidebarCollapsed;

    public MainViewModel(
        CaseAnalysisService analysis,
        LogInboxService inbox,
        SettingsService settings,
        ReportService reports,
        WorkbenchLogger logger,
        Func<string, string?> applyTheme,
        DataPaths paths,
        BootstrapConfigurationStore bootstrapStore,
        Func<string?> startReplacementProcess,
        Action shutdownCurrentProcess,
        IExtensionCenterService extensions,
        Action<ExtensionManifest> openWorkspace,
        SshTerminalViewModel sshTerminal)
    {
        _inbox = inbox;
        _logger = logger;
        _settings = settings;
        _directoryOpen = new DirectoryOpenService(logger);
        NavigationSections = ShellNavigation.CreateFixed();
        AnalysisCenter = new AnalysisCenterViewModel(inbox, analysis, reports, OpenExtractDirectory, logger);
        SshTerminal = sshTerminal;
        OpenGlobalWarningCommand = new DelegateCommand(() => SelectNavigation("extensions"));
        ToggleSidebarCommand = new DelegateCommand(() => _ = ToggleSidebarAsync());
        Settings = new SettingsViewModel(
            settings,
            inbox,
            applyTheme,
            SshTerminal.ApplyPreferences,
            paths,
            bootstrapStore,
            _directoryOpen,
            startReplacementProcess,
            shutdownCurrentProcess);
        Extensions = new ExtensionCenterViewModel(extensions, openWorkspace, logger);
        Settings.ExtensionAllowPrereleaseSaved += OnExtensionAllowPrereleaseSaved;
        _selectedNavigationItem = FindNavigation("analysis");
        _currentPage = AnalysisCenter;
        UpdateStatusMessage();
        _inbox.ConfigurationChanged += OnConfigurationChanged;
        Extensions.StateChanged += OnExtensionStateChanged;
        logger.MessageWritten += OnLogMessage;
    }

    public IReadOnlyList<NavigationSection> NavigationSections { get; }
    public AnalysisCenterViewModel AnalysisCenter { get; }
    public SshTerminalViewModel SshTerminal { get; }
    public SettingsViewModel Settings { get; }
    public ExtensionCenterViewModel Extensions { get; }
    public string GlobalWarningText { get => _globalWarningText; private set { if (SetProperty(ref _globalWarningText, value)) OnPropertyChanged(nameof(HasGlobalWarning)); } }
    public bool HasGlobalWarning => !string.IsNullOrWhiteSpace(GlobalWarningText);
    public string StatusMessage { get; private set; } = string.Empty;
    public string AppVersion => AppVersionInfo.DisplayVersion;
    public string WindowTitle => "Hephaestus工作台";
    public ICommand OpenGlobalWarningCommand { get; }
    /// <summary>切换全局工作台导航的展开与紧凑图标状态。</summary>
    public ICommand ToggleSidebarCommand { get; }
    public bool IsSidebarCollapsed
    {
        get => _isSidebarCollapsed;
        private set
        {
            if (!SetProperty(ref _isSidebarCollapsed, value)) return;
            OnPropertyChanged(nameof(SidebarWidth));
        }
    }
    public Wpf.GridLength SidebarWidth => new(IsSidebarCollapsed ? 64 : 184);

    public NavigationItem? SelectedNavigationItem
    {
        get => _selectedNavigationItem;
        set
        {
            if (!SetProperty(ref _selectedNavigationItem, value) || value is null) return;
            CurrentPage = value.Key switch
            {
                "analysis" => AnalysisCenter,
                "ssh" => SshTerminal,
                "extensions" => Extensions,
                "settings" => Settings,
                _ => AnalysisCenter
            };
        }
    }

    public object? CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (!SetProperty(ref _currentPage, value)) return;
            OnPropertyChanged(nameof(PageTitle));
            OnPropertyChanged(nameof(PageContext));
        }
    }
    public string PageTitle => SelectedNavigationItem?.Title ?? "Hephaestus工作台";
    public string PageContext => string.Empty;

    public async Task InitializeAsync()
    {
        await AnalysisCenter.InitializeAsync();
        await Settings.Initialization;
        IsSidebarCollapsed = await _settings.GetSidebarCollapsedAsync();
        await SshTerminal.InitializeAsync();
        // 默认启动页是分析中心；在线 Catalog 刷新不得阻塞主窗口出现，完成后通过 StateChanged 更新全局告警。
        _extensionRefreshTask = Extensions.InitializeAsync(
            Settings.AutoCheckExtensionUpdates,
            Settings.AllowPrereleaseExtensions,
            _extensionRefreshCancellation.Token);
        RefreshGlobalWarning();
    }

    private void SelectNavigation(string key) => SelectedNavigationItem = FindNavigation(key);
    private NavigationItem FindNavigation(string key)
        => NavigationSections.SelectMany(section => section.Items).First(item => item.Key == key);

    private void OpenExtractDirectory(string path)
    {
        var result = _directoryOpen.OpenExtractDirectory(path);
        if (!result.Succeeded)
            Wpf.MessageBox.Show(result.ErrorMessage ?? "无法打开解压目录。", "无法打开解压目录", Wpf.MessageBoxButton.OK, Wpf.MessageBoxImage.Information);
    }

    private void OnConfigurationChanged(object? sender, EventArgs e) => RunOnUi(UpdateStatusMessage);
    private void OnLogMessage(object? sender, string message) => RunOnUi(() => { StatusMessage = message; OnPropertyChanged(nameof(StatusMessage)); });
    private void OnExtensionStateChanged(object? sender, EventArgs e) => RunOnUi(RefreshGlobalWarning);
    private void OnExtensionAllowPrereleaseSaved(bool allowPrerelease) => Extensions.SetAllowPrerelease(allowPrerelease);
    private async Task ToggleSidebarAsync()
    {
        var next = !IsSidebarCollapsed;
        IsSidebarCollapsed = next;
        try
        {
            await _settings.SetSidebarCollapsedAsync(next);
        }
        catch (Exception exception)
        {
            IsSidebarCollapsed = !next;
            _logger.Error($"保存工作台侧边栏状态失败：{exception.Message}");
        }
    }

    private void RefreshGlobalWarning()
        => GlobalWarningText = Extensions.HasEnabledAnalysisEngine ? string.Empty : "没有已启用的日志分析扩展";

    private void UpdateStatusMessage()
    {
        var directories = string.Join("、", _inbox.WatchDirectories);
        StatusMessage = _inbox.IsUsingDefaultDirectory ? $"监控：默认收件目录 {directories}" : $"监控：{directories}";
        OnPropertyChanged(nameof(StatusMessage));
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Wpf.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action(); else _ = dispatcher.InvokeAsync(action);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 在线 Catalog 刷新不阻塞主窗口，但仍由 Shell 持有取消权，避免窗口关闭后继续回调 UI。
        _extensionRefreshCancellation.Cancel();
        _extensionRefreshCancellation.Dispose();
        _inbox.ConfigurationChanged -= OnConfigurationChanged;
        Settings.ExtensionAllowPrereleaseSaved -= OnExtensionAllowPrereleaseSaved;
        Extensions.StateChanged -= OnExtensionStateChanged;
        _logger.MessageWritten -= OnLogMessage;
        AnalysisCenter.Dispose();
        SshTerminal.Dispose();
    }
}
