using System.Diagnostics;
using System.IO;
using System.Windows;
using HephaestusWorkbench.App.ViewModels;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Repositories;
using HephaestusWorkbench.Core.Services;
using HephaestusWorkbench.PluginSDK;
using HephaestusWorkbench.App.Views;
using HephaestusWorkbench.Data;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.App;

public partial class App : System.Windows.Application
{
    private WorkbenchHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        // 部分显卡驱动或虚拟显示环境会让 WPF 硬件合成结果为空白，但控件仍存在于自动化树中。
        // 工作台以表单和文本为主，启动时使用软件合成可保证主窗口稳定绘制；WebView2 仍由自身渲染。
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        base.OnStartup(e);
        if (e.Args.Any(x => string.Equals(x, "--uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            try { UninstallManager.Run(); }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"卸载失败：{ex.Message}", "Hephaestus工作台", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            Shutdown(0);
            return;
        }
        try
        {
            _host = await WorkbenchHost.CreateAsync();
            if (_host is null)
            {
                Shutdown(0);
                return;
            }
            if (ThemeManager.ApplyTheme(_host.AppSettings.Theme) is { } themeError)
                _host.Logger.Error($"主题加载失败，继续使用当前主题：{themeError}");
            var window = new MainWindow(_host.MainViewModel);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Hephaestus工作台启动失败：{ex.Message}", "Hephaestus工作台", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        base.OnExit(e);
    }
}

/// <summary>
/// 工作台启动组合根，负责按顺序组装基础设施和业务服务。
/// 初始化过程必须保持异步，避免在 WPF UI 线程上同步等待数据库或目录 IO。
/// </summary>
internal sealed class WorkbenchHost : IDisposable
{
    private readonly SqliteConnectionFactory _factory;

    private WorkbenchHost(string dataRoot, BootstrapConfigurationStore bootstrapStore)
    {
        BootstrapStore = bootstrapStore;
        Paths = new DataPaths(dataRoot);
        Paths.EnsureCreated();
        Logger = new WorkbenchLogger(dataRoot);
        _factory = new SqliteConnectionFactory(Paths);

        CasesRepository = new SqliteCaseRepository(_factory);
        TasksRepository = new SqliteTaskRepository(_factory);
        ReportsRepository = new SqliteReportRepository(_factory);
        LifecycleRepository = new SqliteAnalysisLifecycleRepository(_factory);
        Configuration = new WorkbenchConfigurationService(Paths);
        ExtensionSettings = new ExtensionSettingsStore(Paths);
        var extensionHealthChecker = new ExtensionHealthChecker();
        ExtensionTrustStore = new ExtensionTrustStore();
        ExtensionRegistry = new ExtensionRegistry(Paths.ExtensionsDirectory, extensionHealthChecker, ExtensionTrustStore);
        ExtensionPackageVerifier = new ExtensionPackageVerifier(ExtensionTrustStore);
        var hostVersion = AppVersionInfo.DisplayVersion.TrimStart('v');
        var extensionHostCompatibility = new ExtensionHostCompatibility(hostVersion);
        ExtensionInstaller = new ExtensionInstaller(Paths.ExtensionsDirectory, ExtensionPackageVerifier, ExtensionRegistry);
        BundledExtensions = new BundledExtensionInitializationService(
            Path.Combine(AppContext.BaseDirectory, "BundledExtensions"),
            ExtensionInstaller,
            ExtensionRegistry,
            ExtensionPackageVerifier,
            extensionHealthChecker,
            extensionHostCompatibility,
            Logger);
        ExtensionCatalogClient = new ExtensionCatalogClient(Paths, Logger);
        ExtensionCenter = new ExtensionCenterService(
            ExtensionCatalogClient,
            ExtensionInstaller,
            ExtensionRegistry,
            ExtensionSettings,
            Logger,
            hostVersion);
        AnalysisProcessHost = new AnalysisProcessHost(Logger);
        Rules = new RuleSetService(Paths, Logger);

        TaskCenter = new TaskCenter(TasksRepository, Logger);
        Analysis = new CaseAnalysisService(Paths, CasesRepository, TasksRepository, ReportsRepository, ExtensionRegistry, ExtensionSettings, AnalysisProcessHost, TaskCenter, Logger, Rules, LifecycleRepository, hostVersion);
        Reports = new ReportService(ReportsRepository, Analysis);
        Inbox = new LogInboxService(new LogFileParser(), new ArchiveValidator(), Configuration, Logger, Paths.InboxDirectory);
        Settings = new SettingsService(Configuration, Paths.InboxDirectory);
        SshDevices = new SqliteSshDeviceRepository(_factory);
        SshHostKeys = new SqliteSshHostKeyRepository(_factory);
        SshHistory = new SqliteSshConnectionHistoryRepository(_factory);
        MaintenanceOperations = new SqliteMaintenanceOperationRepository(_factory);
        Credentials = new WindowsCredentialStore();
        SshTerminalService = new SshNetTerminalService(SshHostKeys, Credentials);
        CommandExecutionService = new SshNetCommandExecutionService(SshHostKeys, Credentials);
        MaintenanceDiscovery = new LinuxMaintenanceDiscoveryService(CommandExecutionService);
        MaintenancePolicy = new MaintenancePolicy();
        MaintenanceExecutor = new MaintenanceExecutor(
            Paths,
            SshDevices,
            MaintenanceDiscovery,
            MaintenancePolicy,
            CommandExecutionService,
            MaintenanceOperations);
    }

    public BootstrapConfigurationStore BootstrapStore { get; }
    public DataPaths Paths { get; }
    public WorkbenchLogger Logger { get; }
    public IAnalysisCaseRepository CasesRepository { get; }
    public IAnalysisTaskRepository TasksRepository { get; }
    public IReportRepository ReportsRepository { get; }
    public IAnalysisLifecycleRepository LifecycleRepository { get; }
    public WorkbenchConfigurationService Configuration { get; }
    public ExtensionSettingsStore ExtensionSettings { get; }
    public ExtensionRegistry ExtensionRegistry { get; }
    public IExtensionTrustStore ExtensionTrustStore { get; }
    public IExtensionPackageVerifier ExtensionPackageVerifier { get; }
    public ExtensionInstaller ExtensionInstaller { get; }
    public BundledExtensionInitializationService BundledExtensions { get; }
    public ExtensionCatalogClient ExtensionCatalogClient { get; }
    public IExtensionCenterService ExtensionCenter { get; }
    public AnalysisProcessHost AnalysisProcessHost { get; }
    public RuleSetService Rules { get; }
    public TaskCenter TaskCenter { get; }
    public CaseAnalysisService Analysis { get; }
    public ReportService Reports { get; }
    public LogInboxService Inbox { get; }
    public SettingsService Settings { get; }
    public ISshDeviceRepository SshDevices { get; }
    public ISshHostKeyRepository SshHostKeys { get; }
    public ISshConnectionHistoryRepository SshHistory { get; }
    public IMaintenanceOperationRepository MaintenanceOperations { get; }
    public ICredentialStore Credentials { get; }
    public ISshTerminalService SshTerminalService { get; }
    public ICommandExecutionService CommandExecutionService { get; }
    public IMaintenanceDiscoveryService MaintenanceDiscovery { get; }
    public IMaintenancePolicy MaintenancePolicy { get; }
    public IMaintenanceExecutor MaintenanceExecutor { get; }
    public AppSettingsConfig AppSettings { get; private set; } = new();
    public MainViewModel MainViewModel { get; private set; } = null!;

    public static async Task<WorkbenchHost?> CreateAsync()
    {
        var bootstrapDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HephaestusWorkbench");
        Directory.CreateDirectory(bootstrapDirectory);
        var bootstrapStore = new BootstrapConfigurationStore(Path.Combine(bootstrapDirectory, "bootstrap.json"));
        var bootstrap = await bootstrapStore.ReadAsync();
        var selectedRoot = bootstrap.DataRoot
                           ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "HephaestusWorkbenchData");
        var gate = new WorkspaceVersionGate();
        var inspection = bootstrap.Status == BootstrapReadStatus.Legacy
            ? new WorkspaceVersionResult(WorkspaceVersionStatus.Legacy, Path.GetFullPath(selectedRoot))
            : await gate.InspectAsync(selectedRoot);

        // 旧工作区必须在向导或数据库初始化之前被阻断，保证启动检查不会写入任何旧文件。
        if (inspection.Status == WorkspaceVersionStatus.Legacy)
        {
            new LegacyWorkspaceWindow(inspection.DataRoot).ShowDialog();
            return null;
        }

        var dataRoot = inspection.DataRoot;
        if (inspection.Status == WorkspaceVersionStatus.Empty)
        {
            var wizardDataRoot = dataRoot;
            var wizard = new FirstRunWizard(
                dataRoot,
                (root, monitorPaths, progress) =>
                {
                    wizardDataRoot = root;
                    return new WorkbenchInitializationService().InitializeAsync(root, monitorPaths, progress);
                });
            if (wizard.ShowDialog() != true) return null;
            dataRoot = wizardDataRoot;
            await bootstrapStore.WriteAsync(dataRoot);
        }

        var host = new WorkbenchHost(dataRoot, bootstrapStore);
        try
        {
            await host.InitializeAsync();
            return host;
        }
        catch (Exception ex)
        {
            host.Logger.Error("工作台启动初始化失败", ex);
            host.Dispose();
            throw;
        }
    }

    private async Task InitializeAsync()
    {
        // 所有可能产生异步 IO 的启动步骤集中在这里，并由 OnStartup 使用 await 调用。
        Logger.Info("开始初始化工作台数据库。");
        await new DatabaseInitializer(_factory).InitializeAsync();
        Logger.Info("工作台数据库初始化完成。");
        var recoveredTasks = await LifecycleRepository.RecoverInterruptedAsync(DateTime.Now);
        if (recoveredTasks > 0)
            Logger.Info($"已恢复上次未完成的分析任务：{recoveredTasks} 个。");
        var recoveredMaintenanceOperations = await MaintenanceOperations.RecoverInterruptedAsync(DateTime.Now);
        if (recoveredMaintenanceOperations > 0)
            Logger.Info($"检测到上次未确认结束的维护操作：{recoveredMaintenanceOperations} 个，已统一标记为 OutcomeUnknown，未自动重放。");

        await Configuration.EnsureWorkspaceAsync();
        AppSettings = await Configuration.EnsureAppSettingsAsync();
        await ExtensionSettings.EnsureAsync();
        await InitializeBundledExtensionsAsync();

        Logger.Info("开始启动日志收件箱监控。");
        await Inbox.StartAsync();
        Logger.Info(Inbox.IsConfigured
            ? $"日志收件箱监控已启动：{string.Join("、", Inbox.WatchDirectories)}"
            : "未配置日志收件目录，日志收件箱暂不扫描。");

        MainViewModel = new MainViewModel(
            Analysis,
            Inbox,
            Settings,
            Reports,
            Logger,
            ThemeManager.ApplyTheme,
            Paths,
            BootstrapStore,
            StartReplacementProcess,
            () => System.Windows.Application.Current.Shutdown(0),
            ExtensionCenter,
            OpenWorkspaceExtension,
            new SshTerminalViewModel(
                SshTerminalService,
                SshDevices,
                SshHostKeys,
                SshHistory,
                Credentials,
                new WpfHostKeyConfirmationService(),
                AppSettings,
                Path.Combine(Paths.CacheDirectory, "TerminalWebView2"),
                OpenMaintenanceWorkspace));
        await MainViewModel.InitializeAsync();
        Logger.Info("工作台初始化完成。");
    }

    /// <summary>
    /// 先启动使用新 bootstrap 指针的替代进程；只有启动成功后，设置页才会关闭当前进程。
    /// 返回值为空表示启动成功，非空值为可直接展示的中文失败原因。
    /// </summary>
    private static string? StartReplacementProcess()
    {
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable)) return "无法确定当前工作台程序路径。";
            return Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true }) is null
                ? "系统未能启动新的工作台进程。"
                : null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// 正式安装包必须携带锁定 Bundle；普通源码构建允许缺少该目录，便于在正式签名资产尚未注入时开发宿主。
    /// 两种构建都不提供 unsigned/developer extension mode：只要目录存在，就必须通过同一正式验签与安装链路。
    /// </summary>
    private async Task InitializeBundledExtensionsAsync()
    {
        var bundleRoot = Path.Combine(AppContext.BaseDirectory, "BundledExtensions");
        if (!Directory.Exists(bundleRoot) && !RequireBundledExtensions)
        {
            Logger.Info($"当前源码构建未携带 BundledExtensions，跳过离线扩展初始化：{Path.GetFullPath(bundleRoot)}");
            return;
        }

        Logger.Info("开始验证并部署安装包内置扩展。");
        await BundledExtensions.InitializeAsync();
        Logger.Info("安装包内置扩展初始化完成。");
    }

#if REQUIRE_BUNDLED_EXTENSIONS
    private const bool RequireBundledExtensions = true;
#else
    private const bool RequireBundledExtensions = false;
#endif

    /// <summary>
    /// 从 SSH 设备上下文打开维护窗口。此入口当前只提供该设备的操作历史；
    /// 未来由受信宿主代码生成计划时，仍复用同一窗口并传入不可变 ExecutionPlan。
    /// </summary>
    private void OpenMaintenanceWorkspace(SshDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var viewModel = new MaintenanceWorkspaceViewModel(
            device.Id,
            null,
            MaintenanceExecutor,
            MaintenanceOperations,
            Paths,
            text => System.Windows.Clipboard.SetText(text));
        var window = new MaintenanceWorkspaceWindow(viewModel)
        {
            Owner = System.Windows.Application.Current.MainWindow,
            Title = $"{device.Name} · 维护记录"
        };
        window.Show();
    }

    /// <summary>
    /// Workspace 扩展只能进入宿主固定窗口。窗口持有实际打开版本的租约，关闭前该版本不能被清理。
    /// 扩展中心快照可能已经过期，因此以 Registry 当前 healthy 版本为准，并再次核对发布者与类别身份。
    /// </summary>
    private void OpenWorkspaceExtension(ExtensionManifest manifest)
    {
        var lease = ExtensionRegistry.LeaseCurrentVersion(manifest.Id);
        try
        {
            if (!string.Equals(lease.Manifest.PublisherId, manifest.PublisherId, StringComparison.Ordinal) ||
                lease.Manifest.Kind != manifest.Kind)
            {
                throw new InvalidOperationException($"扩展 {manifest.Id} 的当前版本身份已变化，请刷新扩展中心后重试。");
            }

            var window = new WorkspaceHostWindow(lease, Paths.CacheDirectory, Logger)
            {
                Owner = System.Windows.Application.Current.MainWindow
            };
            window.Show();
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        MainViewModel?.Dispose();
        Inbox.Dispose();
    }

}
