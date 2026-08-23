using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Data;
using HephaestusWorkbench.App.Views;
using HephaestusWorkbench.PluginSDK;
using HephaestusWorkbench.Services;
using Microsoft.Win32;
using Wpf = System.Windows;

namespace HephaestusWorkbench.App.ViewModels;

public sealed class InstalledPluginItem
{
    public required PluginManifest Manifest { get; init; }
    public required PluginInstallSource Source { get; init; }
    public bool IsRuleEditor => Manifest.Supports("rule-editor");
    public bool IsStandaloneTool => Manifest.Type == PluginType.Web && Manifest.Supports("standalone-tool");
    public bool IsLogAnalyzer => Manifest.Type == PluginType.Exe && string.Equals(Manifest.Id, "log-analyzer", StringComparison.OrdinalIgnoreCase);
    public bool CanUpdateRules => IsLogAnalyzer;
    public bool CanSetDefault => !IsStandaloneTool && !IsDefault;
    public bool Enabled { get; init; }
    public bool IsDefault { get; init; }
    public string SourceText => Source switch { PluginInstallSource.Bundled => "内置", PluginInstallSource.Marketplace => "在线安装", _ => "手工安装" };
    public string StatusText => IsStandaloneTool ? (Enabled ? "工具可启动" : "工具已禁用") : IsDefault ? "默认插件" : Enabled ? "已启用" : "已禁用";
    public bool CanUninstall => Source == PluginInstallSource.Marketplace && !IsDefault;
    public string ToggleText => Enabled ? "禁用" : "启用";
}

public sealed class OnlinePluginItem
{
    public required MarketplacePlugin Plugin { get; init; }
    public string? InstalledVersion { get; init; }
    public bool IsCompatible { get; init; }
    public bool IsInstalled => InstalledVersion is not null;
    public bool HasUpdate => IsInstalled && Version.TryParse(Plugin.Version, out var online) && Version.TryParse(InstalledVersion, out var local) && online > local;
    public string ActionText => !IsCompatible ? "版本不兼容" : !IsInstalled ? "安装" : HasUpdate ? "更新" : "已是最新";
    public bool CanInstall => IsCompatible && (!IsInstalled || HasUpdate);
    public string VersionText => IsInstalled ? $"本地 {InstalledVersion} / 在线 {Plugin.Version}" : $"在线版本 {Plugin.Version}";
    public string DeveloperText => string.IsNullOrWhiteSpace(Plugin.Author) ? "未知开发者" : Plugin.Author;
    public string CategoryText => string.IsNullOrWhiteSpace(Plugin.Category) ? "其他" : Plugin.Category;
    public string TypeText => Plugin.Type == PluginType.Web ? "Web 工具" : "EXE 插件";
    public string DescriptionText => string.IsNullOrWhiteSpace(Plugin.Description) ? "暂无应用描述。" : Plugin.Description;
}

/// <summary>
/// 在线应用中心视图模型。文件写入和安全校验均委托给服务层，界面只组合在线目录、本地状态和用户操作。
/// </summary>
public sealed class MarketplacePluginsViewModel : ViewModelBase
{
    private readonly PluginCatalog _catalog;
    private readonly PluginMarketplaceService _marketplace;
    private readonly SettingsService _settings;
    private readonly WorkbenchLogger _logger;
    private readonly RuleSetService _rules;
    private readonly IRuleDistributionService _ruleDistribution;
    private readonly IRulePublisher _publisher;
    private bool _isBusy;
    private double _progressValue;
    private bool _isProgressIndeterminate;
    private string _message = "正在加载应用中心…";
    private DateTime? _lastRefresh;
    private RuleStateSnapshot _ruleState = new();
    private string _selectedTab = "发现应用";
    private string _searchText = string.Empty;
    private string _selectedCategory = "全部";
    private string _selectedSortMode = "名称";
    private OnlinePluginItem? _selectedOnlineItem;
    private bool _isToolsAndResourcesOpen;
    private bool _isOffline;
    private string _catalogStatusText = "目录状态：尚未刷新";
    private bool _isDownloadSettingsOpen;
    private bool _downloadSettingsLoaded;
    private string _githubDownloadMirrorTemplate = string.Empty;
    private string _downloadSettingsMessage = string.Empty;

    public MarketplacePluginsViewModel(PluginCatalog catalog, PluginMarketplaceService marketplace, SettingsService settings, WorkbenchLogger logger, RuleSetService rules, IRuleDistributionService ruleDistribution, IRulePublisher publisher)
    {
        _catalog = catalog;
        _marketplace = marketplace;
        _settings = settings;
        _logger = logger;
        _rules = rules;
        _ruleDistribution = ruleDistribution;
        _publisher = publisher;
        RefreshCommand = new DelegateCommand(() => _ = LoadAsync(), () => !IsBusy);
        SelectDiscoveryTabCommand = new DelegateCommand(() => SelectedTab = "发现应用");
        SelectInstalledTabCommand = new DelegateCommand(() => SelectedTab = "已安装");
        SelectOnlinePluginCommand = new DelegateCommand(value => SelectedOnlineItem = value as OnlinePluginItem);
        CloseDetailsCommand = new DelegateCommand(() => SelectedOnlineItem = null);
        OpenToolsAndResourcesCommand = new DelegateCommand(() => IsToolsAndResourcesOpen = !IsToolsAndResourcesOpen);
        SaveDownloadSettingsCommand = new DelegateCommand(() => _ = SaveDownloadSettingsAsync(), () => !IsBusy);
        ClearGitHubDownloadMirrorCommand = new DelegateCommand(() => GitHubDownloadMirrorTemplate = string.Empty);
        InstallCommand = new DelegateCommand(value => _ = InstallAsync((OnlinePluginItem)value!), value => !IsBusy && value is OnlinePluginItem item && item.CanInstall);
        SetDefaultCommand = new DelegateCommand(value => _ = SetDefaultAsync((InstalledPluginItem)value!), value => !IsBusy && value is InstalledPluginItem item && item.CanSetDefault);
        ToggleEnabledCommand = new DelegateCommand(value => _ = ToggleEnabledAsync((InstalledPluginItem)value!), value => !IsBusy && value is InstalledPluginItem item && !(item.IsDefault && item.Enabled));
        UninstallCommand = new DelegateCommand(value => _ = UninstallAsync((InstalledPluginItem)value!), value => !IsBusy && value is InstalledPluginItem item && item.CanUninstall);
        OpenPluginDirectoryCommand = new DelegateCommand(() => OpenPath(_catalog.ExtensionsDirectory, true));
        OpenDocumentationCommand = new DelegateCommand(() => OpenPath(Path.Combine(AppContext.BaseDirectory, "Documentation", "plugin-development.md"), false));
        UseRuleEditorCommand = new DelegateCommand(value => UseRuleEditor((InstalledPluginItem)value!), value => !IsBusy && value is InstalledPluginItem item && item.IsRuleEditor);
        LaunchToolCommand = new DelegateCommand(value => LaunchTool((InstalledPluginItem)value!), value => !IsBusy && value is InstalledPluginItem item && item.IsStandaloneTool && item.Enabled);
        ImportRuleCommand = new DelegateCommand(_ => _ = ImportRuleAsync(), _ => !IsBusy);
        UploadRuleCommand = new DelegateCommand(_ => _ = UploadRuleAsync(), _ => !IsBusy && _publisher.IsConfigured && _rules.HasActiveRules);
        OpenRulesDirectoryCommand = new DelegateCommand(() => OpenPath(_rules.RulesDirectory, true));
        UpdateRulesCommand = new DelegateCommand(_ => _ = UpdateRulesAsync(), _ => !IsBusy);
        _ = LoadAsync();
    }

    public ObservableCollection<InstalledPluginItem> InstalledItems { get; } = new();
    public ObservableCollection<OnlinePluginItem> OnlineItems { get; } = new();
    public ObservableCollection<OnlinePluginItem> FilteredOnlineItems { get; } = new();
    public ObservableCollection<string> Issues { get; } = new();
    public IReadOnlyList<string> Categories { get; } = new[] { "全部", "日志分析", "规则工具", "运维工具", "其他" };
    public IReadOnlyList<string> SortModes { get; } = new[] { "名称", "版本", "已安装优先" };
    public ICommand RefreshCommand { get; }
    public ICommand SelectDiscoveryTabCommand { get; }
    public ICommand SelectInstalledTabCommand { get; }
    public ICommand SelectOnlinePluginCommand { get; }
    public ICommand CloseDetailsCommand { get; }
    public ICommand OpenToolsAndResourcesCommand { get; }
    public ICommand SaveDownloadSettingsCommand { get; }
    public ICommand ClearGitHubDownloadMirrorCommand { get; }
    public ICommand InstallCommand { get; }
    public ICommand SetDefaultCommand { get; }
    public ICommand ToggleEnabledCommand { get; }
    public ICommand UninstallCommand { get; }
    public ICommand OpenPluginDirectoryCommand { get; }
    public ICommand OpenDocumentationCommand { get; }
    public ICommand UseRuleEditorCommand { get; }
    public ICommand LaunchToolCommand { get; }
    public ICommand ImportRuleCommand { get; }
    public ICommand UploadRuleCommand { get; }
    public ICommand OpenRulesDirectoryCommand { get; }
    public ICommand UpdateRulesCommand { get; }
    public bool CanUploadRules => _publisher.IsConfigured && _ruleState.LocalRuleCount > 0;
    public string UploadRulesHint => _publisher.IsConfigured ? (_ruleState.LocalRuleCount > 0 ? $"提交用户规则（待审核 {_ruleState.PendingRuleCount} 条）" : "暂无用户规则") : "未配置 HTTPS 规则发布地址";
    public string AnalysisRuleVersionText => string.IsNullOrWhiteSpace(_ruleState.OfficialVersion) ? "规则版本：未同步" : $"规则版本：{_ruleState.OfficialVersion}";
    public string RuleStatusText => $"本地自定义：{_ruleState.LocalRuleCount} 条 · 待审核：{_ruleState.PendingRuleCount} 条 · 冲突：{_ruleState.ConflictRuleCount} 条";
    public string SelectedTab { get => _selectedTab; set { if (SetProperty(ref _selectedTab, value)) { OnPropertyChanged(nameof(IsDiscoveryTab)); OnPropertyChanged(nameof(IsInstalledTab)); } } }
    public bool IsDiscoveryTab => string.Equals(SelectedTab, "发现应用", StringComparison.Ordinal);
    public bool IsInstalledTab => !IsDiscoveryTab;
    public string SearchText { get => _searchText; set { if (SetProperty(ref _searchText, value)) ApplyOnlineFilter(); } }
    public string SelectedCategory { get => _selectedCategory; set { if (SetProperty(ref _selectedCategory, value)) ApplyOnlineFilter(); } }
    public string SelectedSortMode { get => _selectedSortMode; set { if (SetProperty(ref _selectedSortMode, value)) ApplyOnlineFilter(); } }
    public OnlinePluginItem? SelectedOnlineItem { get => _selectedOnlineItem; private set { if (SetProperty(ref _selectedOnlineItem, value)) OnPropertyChanged(nameof(IsDetailsOpen)); } }
    public bool IsDetailsOpen => SelectedOnlineItem is not null;
    public bool IsToolsAndResourcesOpen { get => _isToolsAndResourcesOpen; set => SetProperty(ref _isToolsAndResourcesOpen, value); }
    public bool IsDownloadSettingsOpen { get => _isDownloadSettingsOpen; set => SetProperty(ref _isDownloadSettingsOpen, value); }
    public string GitHubDownloadMirrorTemplate { get => _githubDownloadMirrorTemplate; set => SetProperty(ref _githubDownloadMirrorTemplate, value); }
    public string DownloadSettingsMessage { get => _downloadSettingsMessage; private set => SetProperty(ref _downloadSettingsMessage, value); }
    public bool IsOffline { get => _isOffline; private set => SetProperty(ref _isOffline, value); }
    public string CatalogStatusText { get => _catalogStatusText; private set => SetProperty(ref _catalogStatusText, value); }
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) RaiseCommandStates(); } }
    public double ProgressValue { get => _progressValue; private set => SetProperty(ref _progressValue, value); }
    public bool IsProgressIndeterminate { get => _isProgressIndeterminate; private set => SetProperty(ref _isProgressIndeterminate, value); }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public string LastRefreshText => _lastRefresh is null ? "尚未刷新" : $"最后刷新：{_lastRefresh:yyyy-MM-dd HH:mm:ss}";
    public bool ShowIssues => Issues.Count > 0;
    public bool ShowInstalledEmpty => InstalledItems.Count == 0;
    public bool ShowOnlineEmpty => FilteredOnlineItems.Count == 0;
    public event EventHandler? StateChanged;

    private async Task LoadAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        Message = "正在刷新本地与在线插件…";
        try
        {
            if (!_downloadSettingsLoaded)
            {
                GitHubDownloadMirrorTemplate = await _settings.GetGitHubDownloadMirrorTemplateAsync();
                _downloadSettingsLoaded = true;
            }
            await _marketplace.SynchronizePluginInfoAsync();
            var installed = await _catalog.ScanAsync();
            var config = await _marketplace.GetConfigurationAsync();
            InstalledItems.Clear();
            foreach (var plugin in installed.OrderBy(x => x.Name))
            {
                var entry = config.Plugins.FirstOrDefault(x => string.Equals(x.Id, plugin.Id, StringComparison.OrdinalIgnoreCase));
                InstalledItems.Add(new InstalledPluginItem { Manifest = plugin, Source = entry?.Source ?? PluginInstallSource.Manual, Enabled = entry?.Enabled ?? true, IsDefault = string.Equals(config.DefaultPluginId, plugin.Id, StringComparison.OrdinalIgnoreCase) });
            }
            MarketplaceCatalogResult online;
            try
            {
                online = await _marketplace.RefreshAsync();
            }
            catch (Exception ex)
            {
                online = new MarketplaceCatalogResult(Array.Empty<MarketplacePlugin>(), false, $"在线目录暂时不可用：{ex.Message}");
                _logger.Error("在线目录不可用，已保留本地插件管理。", ex);
            }
            _ruleState = await _rules.GetStateAsync();
            var appVersion = typeof(MarketplacePluginsViewModel).Assembly.GetName().Version ?? new Version(1, 1, 2);
            OnlineItems.Clear();
            foreach (var plugin in online.Plugins.OrderBy(x => x.Name))
            {
                var local = installed.FirstOrDefault(x => string.Equals(x.Id, plugin.Id, StringComparison.OrdinalIgnoreCase));
                OnlineItems.Add(new OnlinePluginItem { Plugin = plugin, InstalledVersion = local?.Version, IsCompatible = Version.TryParse(plugin.MinimumAppVersion, out var minimum) && minimum <= appVersion });
            }
            ApplyOnlineFilter();
            Issues.Clear();
            foreach (var issue in _catalog.Issues) Issues.Add(issue);
            foreach (var issue in online.Issues ?? Array.Empty<string>()) Issues.Add(issue);
            _lastRefresh = DateTime.Now;
            IsOffline = online.IsFromCache || (online.Plugins.Count == 0 && online.Warning is not null);
            CatalogStatusText = online.IsFromCache ? "目录状态：离线缓存" : online.Plugins.Count == 0 && online.Warning is not null ? "目录状态：在线目录不可用" : $"目录状态：在线 · {online.Plugins.Count} 个应用";
            Message = online.Warning ?? $"刷新完成：{InstalledItems.Count} 个已安装插件，{OnlineItems.Count} 个在线应用。";
        }
        catch (Exception ex)
        {
            Message = $"刷新应用中心失败：{ex.Message}";
            Issues.Clear();
            Issues.Add(Message);
            _logger.Error("刷新应用中心失败", ex);
        }
        finally { IsBusy = false; NotifyState(); }
    }

    private Task InstallAsync(OnlinePluginItem item)
    {
        var progress = new Progress<PluginInstallProgress>(value =>
        {
            Message = value.Stage;
            IsProgressIndeterminate = value.TotalBytes is null;
            ProgressValue = value.TotalBytes is > 0 ? Math.Clamp(value.BytesReceived * 100d / value.TotalBytes.Value, 0, 100) : 0;
        });
        return RunOperationAsync($"正在{(item.IsInstalled ? "更新" : "安装")} {item.Plugin.Name}…", () => _marketplace.InstallOrUpdateAsync(item.Plugin, progress: progress));
    }
    private Task SetDefaultAsync(InstalledPluginItem item) => RunOperationAsync($"正在将 {item.Manifest.Name} 设为默认插件…", () => _marketplace.SetDefaultAsync(item.Manifest.Id));
    private Task ToggleEnabledAsync(InstalledPluginItem item) => RunOperationAsync($"正在{(item.Enabled ? "禁用" : "启用")} {item.Manifest.Name}…", () => _marketplace.SetEnabledAsync(item.Manifest.Id, !item.Enabled));

    private void UseRuleEditor(InstalledPluginItem item)
    {
        try
        {
            var window = new WebToolWindow(item.Manifest, _logger, _rules, _publisher)
            {
                Owner = Wpf.Application.Current.MainWindow
            };
            window.Show();
            Message = $"已打开规则编辑器：{item.Manifest.Name}";
        }
        catch (Exception ex) { Message = $"启动规则编辑器失败：{ex.Message}"; _logger.Error(Message, ex); }
    }

    private void LaunchTool(InstalledPluginItem item)
    {
        try
        {
            var window = new WebToolWindow(item.Manifest, _logger, _rules, _publisher)
            {
                Owner = Wpf.Application.Current.MainWindow
            };
            window.Show();
            Message = $"已启动工具：{item.Manifest.Name}";
        }
        catch (Exception ex)
        {
            Message = $"启动工具失败：{ex.Message}";
            _logger.Error(Message, ex);
        }
    }

    /// <summary>保存应用商店专用的下载备用地址，避免用户为低频网络配置离开当前页面。</summary>
    private async Task SaveDownloadSettingsAsync()
    {
        try
        {
            var normalized = HephaestusWorkbench.Services.GitHubDownloadMirrorTemplate.ValidateAndNormalize(GitHubDownloadMirrorTemplate);
            await _settings.SetGitHubDownloadMirrorTemplateAsync(normalized);
            GitHubDownloadMirrorTemplate = normalized;
            DownloadSettingsMessage = "下载设置已保存。";
        }
        catch (ArgumentException ex)
        {
            DownloadSettingsMessage = ex.Message;
        }
        catch (Exception ex)
        {
            DownloadSettingsMessage = $"下载设置保存失败：{ex.Message}";
            _logger.Error("下载设置保存失败", ex);
        }
    }

    private async Task ImportRuleAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "规则 JSON (*.json)|*.json|所有文件 (*.*)|*.*", Title = "添加本地规则" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var imported = await _rules.ImportAsync(dialog.FileName);
            await _rules.ActivateAsync(imported.Path);
            Message = $"规则已添加并激活：{imported.Name}";
        }
        catch (Exception ex) { Message = $"添加规则失败：{ex.Message}"; _logger.Error(Message, ex); }
        RaiseCommandStates();
        NotifyState();
    }

    private async Task UploadRuleAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var submission = await _rules.BuildSubmissionAsync();
            var submissionId = await _publisher.PublishAsync(submission);
            await _rules.MarkSubmittedAsync(submission, submissionId);
            _ruleState = await _rules.GetStateAsync();
            Message = string.IsNullOrWhiteSpace(submissionId) ? "用户规则已提交审核。" : $"用户规则已提交审核，编号：{submissionId}";
        }
        catch (Exception ex) { Message = $"上传规则失败：{ex.Message}"; _logger.Error(Message, ex); }
        finally { IsBusy = false; RaiseCommandStates(); NotifyState(); }
    }

    private async Task UpdateRulesAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var result = await _ruleDistribution.UpdateAsync();
            _ruleState = await _rules.GetStateAsync();
            Message = result.Updated
                ? $"分析规则已更新：{result.Version}"
                : "当前分析规则已是最新版本。";
        }
        catch (Exception ex) { Message = $"更新主规则失败：{ex.Message}"; _logger.Error(Message, ex); }
        finally { IsBusy = false; RaiseCommandStates(); NotifyState(); }
    }

    private async Task UninstallAsync(InstalledPluginItem item)
    {
        if (Wpf.MessageBox.Show($"确认卸载插件“{item.Manifest.Name}”吗？插件目录将被删除。", "确认卸载", Wpf.MessageBoxButton.YesNo, Wpf.MessageBoxImage.Warning) != Wpf.MessageBoxResult.Yes) return;
        await RunOperationAsync($"正在卸载 {item.Manifest.Name}…", () => _marketplace.UninstallAsync(item.Manifest.Id));
    }

    private async Task RunOperationAsync(string progress, Func<Task> operation)
    {
        if (IsBusy) return;
        IsBusy = true;
        IsProgressIndeterminate = true;
        ProgressValue = 0;
        Message = progress;
        try { await operation(); IsBusy = false; await LoadAsync(); }
        catch (Exception ex) { Message = $"操作失败：{ex.Message}"; _logger.Error("应用中心操作失败", ex); IsBusy = false; }
        finally { IsProgressIndeterminate = false; ProgressValue = 0; }
    }

    private void OpenPath(string path, bool directory)
    {
        try
        {
            if (directory) Directory.CreateDirectory(path);
            if (!Directory.Exists(path) && !File.Exists(path)) throw new FileNotFoundException("目标不存在。", path);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex) { Message = $"打开失败：{ex.Message}"; _logger.Error(Message, ex); }
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(LastRefreshText));
        OnPropertyChanged(nameof(ShowIssues));
        OnPropertyChanged(nameof(ShowInstalledEmpty));
        OnPropertyChanged(nameof(ShowOnlineEmpty));
        OnPropertyChanged(nameof(IsDetailsOpen));
        OnPropertyChanged(nameof(AnalysisRuleVersionText));
        OnPropertyChanged(nameof(RuleStatusText));
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseCommandStates()
    {
        foreach (var command in new[] { RefreshCommand, SaveDownloadSettingsCommand, InstallCommand, SetDefaultCommand, ToggleEnabledCommand, UninstallCommand, UseRuleEditorCommand, LaunchToolCommand, ImportRuleCommand, UploadRuleCommand, UpdateRulesCommand, SelectDiscoveryTabCommand, SelectInstalledTabCommand, SelectOnlinePluginCommand, CloseDetailsCommand, OpenToolsAndResourcesCommand }) ((DelegateCommand)command).RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanUploadRules));
        OnPropertyChanged(nameof(UploadRulesHint));
    }

    private void ApplyOnlineFilter()
    {
        var keyword = SearchText.Trim();
        var query = OnlineItems.Where(item =>
            (SelectedCategory == "全部" || item.CategoryText.Equals(SelectedCategory, StringComparison.OrdinalIgnoreCase))
            && (keyword.Length == 0
                || item.Plugin.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || item.DescriptionText.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || item.DeveloperText.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || item.Plugin.Id.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || item.Plugin.Capabilities.Any(x => x.Contains(keyword, StringComparison.OrdinalIgnoreCase))));

        query = SelectedSortMode switch
        {
            "版本" => query.OrderByDescending(x => x.Plugin.Version, StringComparer.OrdinalIgnoreCase),
            "已安装优先" => query.OrderByDescending(x => x.IsInstalled).ThenBy(x => x.Plugin.Name, StringComparer.OrdinalIgnoreCase),
            _ => query.OrderBy(x => x.Plugin.Name, StringComparer.OrdinalIgnoreCase)
        };

        FilteredOnlineItems.Clear();
        foreach (var item in query) FilteredOnlineItems.Add(item);
        OnPropertyChanged(nameof(ShowOnlineEmpty));
    }
}
