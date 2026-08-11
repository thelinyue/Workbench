using System.Collections.ObjectModel;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Services;
using System.Windows.Input;
using Wpf = System.Windows;

namespace HephaestusWorkbench.App.ViewModels;

/// <summary>工作台 Shell 模型，负责顶层导航和全局运行状态，不承载页面业务。</summary>
public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private readonly CaseAnalysisService _analysis;
    private readonly LogInboxService _inbox;
    private readonly StorageService _storage;
    private readonly PluginCatalog _plugins;
    private object? _currentPage;
    private NavigationItem? _selectedNavigationItem;
    private string _activityStatus = "就绪";
    private string _pluginStatus = "插件：检查中";
    private string _storageStatus = "存储：计算中";

    public MainViewModel(CaseAnalysisService analysis, LogInboxService inbox, StorageService storage, SettingsService settings, PluginCatalog plugins, PluginMarketplaceService marketplace, ReportService reports, WorkbenchLogger logger, Func<string, string?> applyTheme)
    {
        _analysis = analysis;
        _inbox = inbox;
        _storage = storage;
        _plugins = plugins;
        NavigationItems = new ObservableCollection<NavigationItem>
        {
            new("dashboard", "首页", "\uE80F"),
            new("inbox", "日志收件箱", "\uE896"),
            new("cases", "案例", "\uE8B7"),
            new("reports", "报告", "\uE8A5"),
            new("tasks", "任务", "\uE768"),
            new("plugins", "插件", "\uECAA"),
            new("storage", "存储", "\uEDA2"),
            new("settings", "设置", "\uE713")
        };
        Reports = new ReportsWorkspaceViewModel(reports, settings, OpenCase, logger);
        OpenSettingsCommand = new DelegateCommand(OpenSettings);
        Dashboard = new DashboardViewModel(
            analysis,
            storage,
            inbox,
            () => SelectNavigation("inbox"),
            () => SelectNavigation("cases"),
            OpenSettings,
            OpenQuickReportAsync,
            logger);
        Inbox = new InboxViewModel(inbox, analysis);
        Cases = new CasesViewModel(analysis, NavigateToReport);
        Tasks = new TasksViewModel(analysis);
        Storage = new StorageViewModel(storage, analysis);
        Settings = new SettingsViewModel(settings, inbox, () => Reports.OpenTabCount, applyTheme);
        Plugins = new MarketplacePluginsViewModel(plugins, marketplace, logger);
        _selectedNavigationItem = NavigationItems[0];
        _currentPage = Dashboard;
        UpdateStatusMessage();
        _inbox.ConfigurationChanged += OnConfigurationChanged;
        _analysis.StateChanged += OnAnalysisStateChanged;
        logger.MessageWritten += OnLogMessage;
    }

    public ObservableCollection<NavigationItem> NavigationItems { get; }
    public DashboardViewModel Dashboard { get; }
    public InboxViewModel Inbox { get; }
    public CasesViewModel Cases { get; }
    public ReportsWorkspaceViewModel Reports { get; }
    public TasksViewModel Tasks { get; }
    public StorageViewModel Storage { get; }
    public SettingsViewModel Settings { get; }
    public MarketplacePluginsViewModel Plugins { get; }
    public string ActivityStatus { get => _activityStatus; private set => SetProperty(ref _activityStatus, value); }
    public string PluginStatus { get => _pluginStatus; private set => SetProperty(ref _pluginStatus, value); }
    public string StorageStatus { get => _storageStatus; private set => SetProperty(ref _storageStatus, value); }
    public string StatusMessage { get; private set; } = string.Empty;
    public ICommand OpenSettingsCommand { get; }

    public NavigationItem? SelectedNavigationItem
    {
        get => _selectedNavigationItem;
        set
        {
            if (!SetProperty(ref _selectedNavigationItem, value) || value is null) return;
            CurrentPage = value.Key switch
            {
                "inbox" => Inbox,
                "cases" => Cases,
                "reports" => Reports,
                "tasks" => Tasks,
                "storage" => Storage,
                "plugins" => Plugins,
                "settings" => Settings,
                _ => Dashboard
            };
        }
    }

    public object? CurrentPage { get => _currentPage; private set { if (SetProperty(ref _currentPage, value)) OnPropertyChanged(nameof(PageTitle)); } }
    public string PageTitle => SelectedNavigationItem?.Title ?? "赫菲斯托斯工程工作台";

    public async Task InitializeAsync()
    {
        await Reports.InitializeAsync();
        await RefreshHeaderAsync();
    }

    private void NavigateToReport(string caseId, string path)
    {
        SelectedNavigationItem = NavigationItems.First(x => x.Key == "reports");
        _ = Reports.OpenCaseReportAsync(caseId);
    }

    private async Task<bool> OpenQuickReportAsync(string caseId)
    {
        var opened = await Reports.OpenCaseReportAsync(caseId);
        if (opened) SelectedNavigationItem = NavigationItems.First(x => x.Key == "reports");
        return opened;
    }

    private void OpenCase(string caseId)
    {
        SelectedNavigationItem = NavigationItems.First(x => x.Key == "cases");
        _ = Cases.SelectCaseAsync(caseId);
    }

    private void OpenSettings() => SelectedNavigationItem = NavigationItems.First(x => x.Key == "settings");
    private void SelectNavigation(string key) => SelectedNavigationItem = NavigationItems.First(x => x.Key == key);

    private void OnConfigurationChanged(object? sender, EventArgs e) => RunOnUi(UpdateStatusMessage);
    private void OnAnalysisStateChanged(object? sender, EventArgs e) => RunOnUi(() => _ = RefreshHeaderAsync());
    private void OnLogMessage(object? sender, string message) => RunOnUi(() => { StatusMessage = message; OnPropertyChanged(nameof(StatusMessage)); });

    private async Task RefreshHeaderAsync()
    {
        try
        {
            var tasks = await _analysis.ListTasksAsync();
            var cases = await _analysis.ListCasesAsync();
            var current = tasks.FirstOrDefault(x => x.Status is HephaestusWorkbench.Core.Models.TaskStatus.Running or HephaestusWorkbench.Core.Models.TaskStatus.Waiting);
            var currentCase = current is null ? null : cases.FirstOrDefault(x => x.Id == current.CaseId);
            ActivityStatus = current is null ? "就绪" : current.Status == HephaestusWorkbench.Core.Models.TaskStatus.Running ? $"正在分析：{currentCase?.DisplayName ?? current.CaseId}" : $"等待分析：{currentCase?.DisplayName ?? current.CaseId}";
            PluginStatus = $"插件：{(await _plugins.ScanAsync()).Count} 个可用";
            var summary = await _storage.GetSummaryAsync();
            StorageStatus = $"存储：{ViewModelFormatting.Size(summary.TotalBytes)}";
        }
        catch { ActivityStatus = "状态暂不可用"; }
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
        _analysis.StateChanged -= OnAnalysisStateChanged;
        Dashboard.Dispose();
        Reports.Dispose();
    }
}
