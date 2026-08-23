using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using HephaestusWorkbench.PluginSDK;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.App.ViewModels;

/// <summary>扩展中心列表项，只负责把宿主 DTO 转换为稳定的中文显示和操作可见性。</summary>
public sealed class ExtensionCenterItemViewModel
{
    public ExtensionCenterItemViewModel(ExtensionCenterEntry source) => Source = source;

    public ExtensionCenterEntry Source { get; }
    public string Id => Source.Id;
    public string Name => Source.Name;
    public string Description => Source.Description;
    public ExtensionKind Kind => Source.Kind;
    public string KindText => Source.Kind switch
    {
        ExtensionKind.Workspace => "Workspace",
        ExtensionKind.Analysis => "Analysis",
        ExtensionKind.Maintenance => "Maintenance",
        _ => Source.Kind.ToString()
    };
    public string PublisherText => $"发布者：{Source.PublisherId}";
    public string VersionText => Source.InstalledManifest is null
        ? $"可用版本 {Source.AvailableRelease?.Version ?? "—"}"
        : Source.AvailableRelease is not null && Source.HasUpdate
            ? $"已安装 {Source.InstalledManifest.Version} · 可更新至 {Source.AvailableRelease.Version}"
            : $"已安装 {Source.InstalledManifest.Version}";
    public string StatusText => !Source.IsCompatible
        ? "当前版本不兼容"
        : Source.InstalledManifest is null
            ? "未安装"
            : !Source.Enabled
                ? "已禁用"
                : Source.HasUpdate
                    ? "有可用更新"
                    : "已启用";
    public string InstallText => Source.InstalledManifest is null ? "安装" : "更新";
    public bool CanInstall => Source.IsCompatible && Source.AvailableRelease is not null &&
                              (Source.InstalledManifest is null || Source.HasUpdate);
    public bool CanOpen => Source.Enabled && Source.InstalledManifest?.Kind == ExtensionKind.Workspace;
    public string ToggleText => Source.Enabled ? "禁用" : "启用";
    public Visibility InstallVisibility => Source.AvailableRelease is not null &&
                                           (Source.InstalledManifest is null || Source.HasUpdate)
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility OpenVisibility => Source.InstalledManifest?.Kind == ExtensionKind.Workspace
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility ToggleVisibility => Source.InstalledManifest is null
        ? Visibility.Collapsed
        : Visibility.Visible;
}

/// <summary>
/// v2 扩展中心页面模型。固定提供发现、已安装和更新三个视图；类型筛选只改变当前列表，
/// 不向 Shell 注册导航，也不提供默认分析引擎选择。
/// </summary>
public sealed class ExtensionCenterViewModel : ViewModelBase
{
    private const string DiscoveryTab = "discovery";
    private const string InstalledTab = "installed";
    private const string UpdatesTab = "updates";

    private readonly IExtensionCenterService _service;
    private readonly Action<ExtensionManifest> _openWorkspace;
    private readonly WorkbenchLogger _logger;
    private readonly List<ExtensionCenterItemViewModel> _allItems = [];
    private string _selectedTab = DiscoveryTab;
    private string _selectedTypeFilter = "全部";
    private string _searchText = string.Empty;
    private string _message = "尚未加载扩展目录。";
    private string _busyText = "正在加载扩展…";
    private bool _isBusy;

    public ExtensionCenterViewModel(
        IExtensionCenterService service,
        Action<ExtensionManifest> openWorkspace,
        WorkbenchLogger logger)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _openWorkspace = openWorkspace ?? throw new ArgumentNullException(nameof(openWorkspace));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        RefreshCommand = new DelegateCommand(() => _ = LoadAsync(), () => !IsBusy);
        SelectDiscoveryTabCommand = new DelegateCommand(() => SelectTab(DiscoveryTab));
        SelectInstalledTabCommand = new DelegateCommand(() => SelectTab(InstalledTab));
        SelectUpdatesTabCommand = new DelegateCommand(() => SelectTab(UpdatesTab));
        InstallCommand = new DelegateCommand(
            item => _ = InstallAsync(item as ExtensionCenterItemViewModel),
            item => !IsBusy && item is ExtensionCenterItemViewModel extension && extension.CanInstall);
        ToggleEnabledCommand = new DelegateCommand(
            item => _ = ToggleEnabledAsync(item as ExtensionCenterItemViewModel),
            item => !IsBusy && item is ExtensionCenterItemViewModel extension &&
                    extension.Source.InstalledManifest is not null);
        OpenCommand = new DelegateCommand(
            item => Open(item as ExtensionCenterItemViewModel),
            item => !IsBusy && item is ExtensionCenterItemViewModel extension && extension.CanOpen);
    }

    public event EventHandler? StateChanged;

    public ObservableCollection<ExtensionCenterItemViewModel> VisibleItems { get; } = [];
    public IReadOnlyList<string> TypeFilters { get; } = ["全部", "Workspace", "Analysis", "Maintenance"];
    public ICommand RefreshCommand { get; }
    public ICommand SelectDiscoveryTabCommand { get; }
    public ICommand SelectInstalledTabCommand { get; }
    public ICommand SelectUpdatesTabCommand { get; }
    public ICommand InstallCommand { get; }
    public ICommand ToggleEnabledCommand { get; }
    public ICommand OpenCommand { get; }

    public string SelectedTypeFilter
    {
        get => _selectedTypeFilter;
        set
        {
            if (SetProperty(ref _selectedTypeFilter, value)) ApplyFilter();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty)) ApplyFilter();
        }
    }

    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    public string BusyText
    {
        get => _busyText;
        private set => SetProperty(ref _busyText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            RaiseCommandStates();
        }
    }

    public Visibility EmptyVisibility => VisibleItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string EmptyTitle => _selectedTab switch
    {
        InstalledTab => "尚未安装扩展",
        UpdatesTab => "当前没有可用更新",
        _ => "没有匹配的扩展"
    };
    public string EmptyDescription => _selectedTab switch
    {
        InstalledTab => "安装完成的扩展会显示在这里。",
        UpdatesTab => "扩展更新由受信任 Catalog 提供并在安装前完成验签。",
        _ => "请调整搜索关键词或扩展类型筛选。"
    };
    public bool HasEnabledAnalysisEngine => _allItems.Any(item =>
        item.Source.Enabled && item.Source.InstalledManifest is { Kind: ExtensionKind.Analysis } manifest &&
        manifest.Capabilities.Contains("analysis.engine", StringComparer.Ordinal));

    public Task InitializeAsync(CancellationToken cancellationToken = default)
        => LoadAsync(cancellationToken);

    private async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy) return;
        IsBusy = true;
        BusyText = "正在刷新扩展目录…";
        try
        {
            var snapshot = await _service.LoadAsync(cancellationToken);
            _allItems.Clear();
            _allItems.AddRange(snapshot.Extensions
                .Select(entry => new ExtensionCenterItemViewModel(entry))
                .OrderBy(item => item.Name, StringComparer.CurrentCulture));
            Message = snapshot.Warning ?? $"已加载 {_allItems.Count} 个扩展。";
            ApplyFilter();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Message = "扩展目录刷新已取消。";
        }
        catch (Exception exception)
        {
            Message = $"加载扩展中心失败：{exception.Message}";
            _logger.Error(Message, exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task InstallAsync(ExtensionCenterItemViewModel? item)
    {
        if (item is null || !item.CanInstall || IsBusy) return;
        IsBusy = true;
        BusyText = $"正在{item.InstallText}“{item.Name}”…";
        try
        {
            await _service.InstallAsync(new ExtensionCenterInstallRequest
            {
                ExtensionId = item.Id,
                Version = item.Source.AvailableRelease?.Version
            });
            Message = $"扩展“{item.Name}”{item.InstallText}完成。";
        }
        catch (Exception exception)
        {
            Message = $"扩展“{item.Name}”{item.InstallText}失败：{exception.Message}";
            _logger.Error(Message, exception);
            return;
        }
        finally
        {
            IsBusy = false;
        }

        await LoadAsync();
    }

    private async Task ToggleEnabledAsync(ExtensionCenterItemViewModel? item)
    {
        if (item?.Source.InstalledManifest is null || IsBusy) return;
        IsBusy = true;
        var enabled = !item.Source.Enabled;
        BusyText = $"正在{(enabled ? "启用" : "禁用")}“{item.Name}”…";
        try
        {
            await _service.SetEnabledAsync(item.Id, enabled);
            Message = $"扩展“{item.Name}”已{(enabled ? "启用" : "禁用")}。";
        }
        catch (Exception exception)
        {
            Message = $"切换扩展“{item.Name}”状态失败：{exception.Message}";
            _logger.Error(Message, exception);
            return;
        }
        finally
        {
            IsBusy = false;
        }

        await LoadAsync();
    }

    private void Open(ExtensionCenterItemViewModel? item)
    {
        if (item is null || !item.CanOpen || item.Source.InstalledManifest is null) return;
        _openWorkspace(item.Source.InstalledManifest);
    }

    private void SelectTab(string tab)
    {
        if (string.Equals(_selectedTab, tab, StringComparison.Ordinal)) return;
        _selectedTab = tab;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var keyword = SearchText.Trim();
        var items = _allItems.Where(MatchesTab).Where(MatchesType).Where(item =>
            keyword.Length == 0 ||
            item.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
            item.Description.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
            item.Id.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
            item.Source.PublisherId.Contains(keyword, StringComparison.OrdinalIgnoreCase));

        VisibleItems.Clear();
        foreach (var item in items) VisibleItems.Add(item);
        OnPropertyChanged(nameof(EmptyVisibility));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyDescription));
        RaiseCommandStates();
    }

    private bool MatchesTab(ExtensionCenterItemViewModel item) => _selectedTab switch
    {
        InstalledTab => item.Source.InstalledManifest is not null,
        UpdatesTab => item.Source.HasUpdate,
        _ => item.Source.AvailableRelease is not null
    };

    private bool MatchesType(ExtensionCenterItemViewModel item)
        => SelectedTypeFilter == "全部" || string.Equals(item.KindText, SelectedTypeFilter, StringComparison.Ordinal);

    private void RaiseCommandStates()
    {
        ((DelegateCommand)RefreshCommand).RaiseCanExecuteChanged();
        ((DelegateCommand)InstallCommand).RaiseCanExecuteChanged();
        ((DelegateCommand)ToggleEnabledCommand).RaiseCanExecuteChanged();
        ((DelegateCommand)OpenCommand).RaiseCanExecuteChanged();
    }
}
