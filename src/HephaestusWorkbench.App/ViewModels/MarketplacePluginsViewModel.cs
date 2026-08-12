using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Data;
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
    public bool Enabled { get; init; }
    public bool IsDefault { get; init; }
    public string SourceText => Source switch { PluginInstallSource.Bundled => "内置", PluginInstallSource.Marketplace => "在线安装", _ => "手工安装" };
    public string StatusText => IsDefault ? "默认插件" : Enabled ? "已启用" : "已禁用";
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
}

/// <summary>
/// 在线插件中心视图模型。文件写入和安全校验均委托给服务层，界面只组合在线目录、本地状态和用户操作。
/// </summary>
public sealed class MarketplacePluginsViewModel : ViewModelBase
{
    private readonly PluginCatalog _catalog;
    private readonly PluginMarketplaceService _marketplace;
    private readonly WorkbenchLogger _logger;
    private readonly RuleSetService _rules;
    private readonly IRulePublisher _publisher;
    private bool _isBusy;
    private string _message = "正在加载插件中心…";
    private DateTime? _lastRefresh;

    public MarketplacePluginsViewModel(PluginCatalog catalog, PluginMarketplaceService marketplace, WorkbenchLogger logger, RuleSetService rules, IRulePublisher publisher)
    {
        _catalog = catalog;
        _marketplace = marketplace;
        _logger = logger;
        _rules = rules;
        _publisher = publisher;
        RefreshCommand = new DelegateCommand(() => _ = LoadAsync(), () => !IsBusy);
        InstallCommand = new DelegateCommand(value => _ = InstallAsync((OnlinePluginItem)value!), value => !IsBusy && value is OnlinePluginItem item && item.CanInstall);
        SetDefaultCommand = new DelegateCommand(value => _ = SetDefaultAsync((InstalledPluginItem)value!), value => !IsBusy && value is InstalledPluginItem item && !item.IsDefault);
        ToggleEnabledCommand = new DelegateCommand(value => _ = ToggleEnabledAsync((InstalledPluginItem)value!), value => !IsBusy && value is InstalledPluginItem item && !(item.IsDefault && item.Enabled));
        UninstallCommand = new DelegateCommand(value => _ = UninstallAsync((InstalledPluginItem)value!), value => !IsBusy && value is InstalledPluginItem item && item.CanUninstall);
        OpenPluginDirectoryCommand = new DelegateCommand(() => OpenPath(_catalog.PluginsDirectory, true));
        OpenDocumentationCommand = new DelegateCommand(() => OpenPath(Path.Combine(AppContext.BaseDirectory, "Documentation", "plugin-development.md"), false));
        UseRuleEditorCommand = new DelegateCommand(value => _ = UseRuleEditorAsync((InstalledPluginItem)value!), value => !IsBusy && value is InstalledPluginItem item && item.IsRuleEditor);
        ImportRuleCommand = new DelegateCommand(_ => _ = ImportRuleAsync(), _ => !IsBusy);
        UploadRuleCommand = new DelegateCommand(_ => _ = UploadRuleAsync(), _ => !IsBusy && _publisher.IsConfigured && _rules.HasActiveRules);
        OpenRulesDirectoryCommand = new DelegateCommand(() => OpenPath(_rules.RulesDirectory, true));
        _ = LoadAsync();
    }

    public ObservableCollection<InstalledPluginItem> InstalledItems { get; } = new();
    public ObservableCollection<OnlinePluginItem> OnlineItems { get; } = new();
    public ObservableCollection<string> Issues { get; } = new();
    public ICommand RefreshCommand { get; }
    public ICommand InstallCommand { get; }
    public ICommand SetDefaultCommand { get; }
    public ICommand ToggleEnabledCommand { get; }
    public ICommand UninstallCommand { get; }
    public ICommand OpenPluginDirectoryCommand { get; }
    public ICommand OpenDocumentationCommand { get; }
    public ICommand UseRuleEditorCommand { get; }
    public ICommand ImportRuleCommand { get; }
    public ICommand UploadRuleCommand { get; }
    public ICommand OpenRulesDirectoryCommand { get; }
    public bool CanUploadRules => _publisher.IsConfigured && _rules.HasActiveRules;
    public string UploadRulesHint => _publisher.IsConfigured ? (_rules.HasActiveRules ? "上传当前激活规则" : "请先激活本地规则") : "未配置 HTTPS 规则发布地址";
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) RaiseCommandStates(); } }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public string LastRefreshText => _lastRefresh is null ? "尚未刷新" : $"最后刷新：{_lastRefresh:yyyy-MM-dd HH:mm:ss}";
    public bool ShowIssues => Issues.Count > 0;
    public bool ShowInstalledEmpty => InstalledItems.Count == 0;
    public bool ShowOnlineEmpty => OnlineItems.Count == 0;
    public event EventHandler? StateChanged;

    private async Task LoadAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        Message = "正在刷新本地与在线插件…";
        try
        {
            await _marketplace.SynchronizePluginInfoAsync();
            var installed = await _catalog.ScanAsync();
            var config = await _marketplace.GetConfigurationAsync();
            var online = await _marketplace.RefreshAsync();
            InstalledItems.Clear();
            foreach (var plugin in installed.OrderBy(x => x.Name))
            {
                var entry = config.Plugins.FirstOrDefault(x => string.Equals(x.Id, plugin.Id, StringComparison.OrdinalIgnoreCase));
                InstalledItems.Add(new InstalledPluginItem { Manifest = plugin, Source = entry?.Source ?? PluginInstallSource.Manual, Enabled = entry?.Enabled ?? true, IsDefault = string.Equals(config.DefaultPluginId, plugin.Id, StringComparison.OrdinalIgnoreCase) });
            }
            var appVersion = typeof(MarketplacePluginsViewModel).Assembly.GetName().Version ?? new Version(1, 1, 2);
            OnlineItems.Clear();
            foreach (var plugin in online.Plugins.OrderBy(x => x.Name))
            {
                var local = installed.FirstOrDefault(x => string.Equals(x.Id, plugin.Id, StringComparison.OrdinalIgnoreCase));
                OnlineItems.Add(new OnlinePluginItem { Plugin = plugin, InstalledVersion = local?.Version, IsCompatible = Version.TryParse(plugin.MinimumAppVersion, out var minimum) && minimum <= appVersion });
            }
            Issues.Clear();
            foreach (var issue in _catalog.Issues) Issues.Add(issue);
            _lastRefresh = DateTime.Now;
            Message = online.Warning ?? $"刷新完成：{InstalledItems.Count} 个已安装插件，{OnlineItems.Count} 个在线插件。";
        }
        catch (Exception ex)
        {
            Message = $"刷新插件中心失败：{ex.Message}";
            Issues.Clear();
            Issues.Add(Message);
            _logger.Error("刷新插件中心失败", ex);
        }
        finally { IsBusy = false; NotifyState(); }
    }

    private Task InstallAsync(OnlinePluginItem item) => RunOperationAsync($"正在{(item.IsInstalled ? "更新" : "安装")} {item.Plugin.Name}…", () => _marketplace.InstallOrUpdateAsync(item.Plugin));
    private Task SetDefaultAsync(InstalledPluginItem item) => RunOperationAsync($"正在将 {item.Manifest.Name} 设为默认插件…", () => _marketplace.SetDefaultAsync(item.Manifest.Id));
    private Task ToggleEnabledAsync(InstalledPluginItem item) => RunOperationAsync($"正在{(item.Enabled ? "禁用" : "启用")} {item.Manifest.Name}…", () => _marketplace.SetEnabledAsync(item.Manifest.Id, !item.Enabled));

    private async Task UseRuleEditorAsync(InstalledPluginItem item)
    {
        try
        {
            var rulesPath = await _rules.EnsureActiveAsync();
            var arguments = $"--rules \"{rulesPath}\" --listen 127.0.0.1:0 --open";
            Process.Start(new ProcessStartInfo { FileName = item.Manifest.EntryPath, Arguments = arguments, WorkingDirectory = item.Manifest.DirectoryPath, UseShellExecute = true });
            Message = $"已启动规则编辑器：{item.Manifest.Name}";
        }
        catch (Exception ex) { Message = $"启动规则编辑器失败：{ex.Message}"; _logger.Error(Message, ex); }
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
        try
        {
            var rules = await _rules.ReadActiveAsync() ?? throw new InvalidOperationException("请先激活本地规则。");
            await _publisher.PublishAsync(rules);
            Message = "规则上传成功。";
        }
        catch (Exception ex) { Message = $"上传规则失败：{ex.Message}"; _logger.Error(Message, ex); }
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
        Message = progress;
        try { await operation(); IsBusy = false; await LoadAsync(); }
        catch (Exception ex) { Message = $"操作失败：{ex.Message}"; _logger.Error("插件中心操作失败", ex); IsBusy = false; }
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
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseCommandStates()
    {
        foreach (var command in new[] { RefreshCommand, InstallCommand, SetDefaultCommand, ToggleEnabledCommand, UninstallCommand, UseRuleEditorCommand, ImportRuleCommand, UploadRuleCommand }) ((DelegateCommand)command).RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanUploadRules));
        OnPropertyChanged(nameof(UploadRulesHint));
    }
}
