using System.Collections.ObjectModel;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Services;
using System.Windows.Input;
using Wpf = System.Windows;

namespace HephaestusWorkbench.App.ViewModels;

/// <summary>工作台 Shell 模型，负责顶层导航和全局运行状态，不承载页面业务。</summary>
public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private readonly LogInboxService _inbox;
    private readonly PluginCatalog _plugins;
    private readonly DirectoryOpenService _directoryOpen;
    private object? _currentPage;
    private NavigationItem? _selectedNavigationItem;
    private string _globalWarningText = string.Empty;

    public MainViewModel(CaseAnalysisService analysis, LogInboxService inbox, StorageService storage, SettingsService settings, PluginCatalog plugins, PluginMarketplaceService marketplace, ReportService reports, WorkbenchLogger logger, Func<string, string?> applyTheme, RuleSetService rules)
    {
        _inbox = inbox;
        _plugins = plugins;
        _directoryOpen = new DirectoryOpenService(logger);
        NavigationItems = new ObservableCollection<NavigationItem>
        {
            new("dashboard", "首页", "\uE80F"),
            new("analysis", "分析中心", "\uE896"),
            new("plugins", "插件", "\uECAA"),
            new("storage", "存储", "\uEDA2"),
            new("settings", "设置", "\uE713")
        };
        var reportWorkspace = new ReportsWorkspaceViewModel(reports, settings, OpenCase, OpenExtractDirectory, logger);
        AnalysisCenter = new AnalysisCenterViewModel(inbox, analysis, reports, reportWorkspace, OpenExtractDirectory, logger);
        TaskPanel = new TaskPanelViewModel(analysis, OpenCase);
        OpenGlobalWarningCommand = new DelegateCommand(() => SelectNavigation("plugins"));
        Dashboard = new DashboardViewModel(
            analysis,
            storage,
            inbox,
            () => SelectNavigation("analysis"),
            () => SelectNavigation("analysis"),
            OpenSettings,
            OpenQuickReportAsync,
            OpenExtractDirectory,
            logger);
        Storage = new StorageViewModel(storage, analysis);
        Settings = new SettingsViewModel(settings, inbox, () => AnalysisCenter.Reports.OpenTabCount, applyTheme);
        Plugins = new MarketplacePluginsViewModel(plugins, marketplace, logger, rules, new HttpRulePublisher(Environment.GetEnvironmentVariable("HEPHAESTUS_RULE_PUBLISH_URL"), Environment.GetEnvironmentVariable("HEPHAESTUS_RULE_PUBLISH_TOKEN"), logger, protectedTokenPath: rules.RulePublisherTokenPath));
        _selectedNavigationItem = NavigationItems[0];
        _currentPage = Dashboard;
        UpdateStatusMessage();
        _inbox.ConfigurationChanged += OnConfigurationChanged;
        AnalysisCenter.Reports.PropertyChanged += OnReportWorkspacePropertyChanged;
        Plugins.StateChanged += OnPluginStateChanged;
        logger.MessageWritten += OnLogMessage;
    }

    public ObservableCollection<NavigationItem> NavigationItems { get; }
    public DashboardViewModel Dashboard { get; }
    public AnalysisCenterViewModel AnalysisCenter { get; }
    public TaskPanelViewModel TaskPanel { get; }
    public StorageViewModel Storage { get; }
    public SettingsViewModel Settings { get; }
    public MarketplacePluginsViewModel Plugins { get; }
    public string GlobalWarningText { get => _globalWarningText; private set { if (SetProperty(ref _globalWarningText, value)) OnPropertyChanged(nameof(HasGlobalWarning)); } }
    public bool HasGlobalWarning => !string.IsNullOrWhiteSpace(GlobalWarningText);
    public string StatusMessage { get; private set; } = string.Empty;
    public ICommand OpenGlobalWarningCommand { get; }

    public NavigationItem? SelectedNavigationItem
    {
        get => _selectedNavigationItem;
        set
        {
            if (!SetProperty(ref _selectedNavigationItem, value) || value is null) return;
            CurrentPage = value.Key switch
            {
                "analysis" => AnalysisCenter,
                "storage" => Storage,
                "plugins" => Plugins,
                "settings" => Settings,
                _ => Dashboard
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
    public string PageTitle => SelectedNavigationItem?.Title ?? "赫菲斯托斯工程工作台";
    public string PageContext => SelectedNavigationItem?.Key == "analysis" && !AnalysisCenter.Reports.IsLibraryVisible && AnalysisCenter.Reports.SelectedTab is not null
        ? $"· {AnalysisCenter.Reports.SelectedTab.Title}"
        : string.Empty;

    public async Task InitializeAsync()
    {
        await AnalysisCenter.InitializeAsync();
        await TaskPanel.LoadAsync();
        await RefreshGlobalWarningAsync();
    }

    private async Task<bool> OpenQuickReportAsync(string caseId)
    {
        var opened = await AnalysisCenter.OpenCaseReportAsync(caseId);
        if (opened) SelectedNavigationItem = NavigationItems.First(x => x.Key == "analysis");
        return opened;
    }

    private void OpenCase(string caseId)
    {
        SelectedNavigationItem = NavigationItems.First(x => x.Key == "analysis");
        _ = AnalysisCenter.SelectCaseAsync(caseId);
    }

    private void OpenSettings() => SelectedNavigationItem = NavigationItems.First(x => x.Key == "settings");
    private void SelectNavigation(string key) => SelectedNavigationItem = NavigationItems.First(x => x.Key == key);

    private void OpenExtractDirectory(string path)
    {
        var result = _directoryOpen.OpenExtractDirectory(path);
        if (!result.Succeeded)
            Wpf.MessageBox.Show(result.ErrorMessage ?? "无法打开解压目录。", "无法打开解压目录", Wpf.MessageBoxButton.OK, Wpf.MessageBoxImage.Information);
    }

    private void OnConfigurationChanged(object? sender, EventArgs e) => RunOnUi(UpdateStatusMessage);
    private void OnLogMessage(object? sender, string message) => RunOnUi(() => { StatusMessage = message; OnPropertyChanged(nameof(StatusMessage)); });
    private void OnPluginStateChanged(object? sender, EventArgs e) => RunOnUi(() => _ = RefreshGlobalWarningAsync());
    private void OnReportWorkspacePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ReportsWorkspaceViewModel.SelectedTab) or nameof(ReportsWorkspaceViewModel.IsLibraryVisible))
            RunOnUi(() => OnPropertyChanged(nameof(PageContext)));
    }

    private async Task RefreshGlobalWarningAsync()
    {
        try
        {
            GlobalWarningText = (await _plugins.ScanAsync()).Count == 0 ? "没有可用的日志分析插件" : string.Empty;
        }
        catch (Exception ex) { GlobalWarningText = $"读取插件状态失败：{ex.Message}"; }
    }

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
        _inbox.ConfigurationChanged -= OnConfigurationChanged;
        AnalysisCenter.Reports.PropertyChanged -= OnReportWorkspacePropertyChanged;
        Plugins.StateChanged -= OnPluginStateChanged;
        Dashboard.Dispose();
        TaskPanel.Dispose();
        AnalysisCenter.Dispose();
    }
}
