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

    public MainViewModel(CaseAnalysisService analysis, LogInboxService inbox, StorageService storage, SettingsService settings, PluginCatalog plugins, PluginMarketplaceService marketplace, ReportService reports, WorkbenchLogger logger, Func<string, string?> applyTheme, RuleSetService rules, IRuleDistributionService ruleDistribution)
    {
        _inbox = inbox;
        _plugins = plugins;
        _directoryOpen = new DirectoryOpenService(logger);
        NavigationSections = ShellNavigation.CreateFixed();
        AnalysisCenter = new AnalysisCenterViewModel(inbox, analysis, reports, OpenExtractDirectory, logger);
        SshTerminal = new SshTerminalViewModel();
        OpenGlobalWarningCommand = new DelegateCommand(() => SelectNavigation("extensions"));
        Settings = new SettingsViewModel(settings, inbox, applyTheme);
        Plugins = new MarketplacePluginsViewModel(plugins, marketplace, settings, logger, rules, ruleDistribution, new HttpRulePublisher(Environment.GetEnvironmentVariable("HEPHAESTUS_RULE_PUBLISH_URL"), Environment.GetEnvironmentVariable("HEPHAESTUS_RULE_PUBLISH_TOKEN"), logger, protectedTokenPath: rules.RulePublisherTokenPath));
        _selectedNavigationItem = FindNavigation("analysis");
        _currentPage = AnalysisCenter;
        UpdateStatusMessage();
        _inbox.ConfigurationChanged += OnConfigurationChanged;
        Plugins.StateChanged += OnPluginStateChanged;
        logger.MessageWritten += OnLogMessage;
    }

    public IReadOnlyList<NavigationSection> NavigationSections { get; }
    public AnalysisCenterViewModel AnalysisCenter { get; }
    public SshTerminalViewModel SshTerminal { get; }
    public SettingsViewModel Settings { get; }
    public MarketplacePluginsViewModel Plugins { get; }
    public string GlobalWarningText { get => _globalWarningText; private set { if (SetProperty(ref _globalWarningText, value)) OnPropertyChanged(nameof(HasGlobalWarning)); } }
    public bool HasGlobalWarning => !string.IsNullOrWhiteSpace(GlobalWarningText);
    public string StatusMessage { get; private set; } = string.Empty;
    public string AppVersion => AppVersionInfo.DisplayVersion;
    public string WindowTitle => "Hephaestus工作台";
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
                "ssh" => SshTerminal,
                "extensions" => Plugins,
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
        await RefreshGlobalWarningAsync();
    }

    private void OpenSettings() => SelectedNavigationItem = FindNavigation("settings");
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
    private void OnPluginStateChanged(object? sender, EventArgs e) => RunOnUi(() => _ = RefreshGlobalWarningAsync());
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
        Plugins.StateChanged -= OnPluginStateChanged;
        AnalysisCenter.Dispose();
    }
}
