using System.IO;
using System.Windows;
using HephaestusWorkbench.App.ViewModels;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Repositories;
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
    private readonly string _seedDirectory;

    private WorkbenchHost(string dataRoot)
    {
        Paths = new DataPaths(dataRoot);
        Paths.EnsureCreated();
        Logger = new WorkbenchLogger(dataRoot);
        _factory = new SqliteConnectionFactory(Paths);

        CasesRepository = new SqliteCaseRepository(_factory);
        TasksRepository = new SqliteTaskRepository(_factory);
        ReportsRepository = new SqliteReportRepository(_factory);
        LifecycleRepository = new SqliteAnalysisLifecycleRepository(_factory);
        Configuration = new WorkbenchConfigurationService(Paths);
        PluginCatalog = new PluginCatalog(Paths, Logger);
        Rules = new RuleSetService(Paths, Logger);
        RuleVerifier = new Ed25519RulePackageVerifier(Rules);
        RuleDistribution = new RuleDistributionService(Rules, RuleVerifier, Logger, plugins: PluginCatalog);

        _seedDirectory = Path.Combine(AppContext.BaseDirectory, "PluginSeed");
        TaskCenter = new TaskCenter(TasksRepository);
        var legacyRunner = new LegacyLogAnalyzerRunner(Logger);
        var standardRunner = new StandardExePluginRunner(Logger);
        Analysis = new CaseAnalysisService(Paths, CasesRepository, TasksRepository, ReportsRepository, PluginCatalog, legacyRunner, standardRunner, TaskCenter, Logger, LifecycleRepository, Configuration, Rules);
        PluginMarketplace = new PluginMarketplaceService(Paths, PluginCatalog, Configuration, TaskCenter, Logger);
        Reports = new ReportService(ReportsRepository, Analysis);
        Inbox = new LogInboxService(new LogFileParser(), new ArchiveValidator(), Configuration, Logger, Paths.InboxDirectory);
        Storage = new StorageService(Paths, CasesRepository, Logger);
        Settings = new SettingsService(Configuration, Paths.InboxDirectory);
    }

    public DataPaths Paths { get; }
    public WorkbenchLogger Logger { get; }
    public IAnalysisCaseRepository CasesRepository { get; }
    public IAnalysisTaskRepository TasksRepository { get; }
    public IReportRepository ReportsRepository { get; }
    public IAnalysisLifecycleRepository LifecycleRepository { get; }
    public WorkbenchConfigurationService Configuration { get; }
    public RuleSetService Rules { get; }
    public IRulePackageVerifier RuleVerifier { get; }
    public IRuleDistributionService RuleDistribution { get; }
    public PluginCatalog PluginCatalog { get; }
    public PluginMarketplaceService PluginMarketplace { get; }
    public TaskCenter TaskCenter { get; }
    public CaseAnalysisService Analysis { get; }
    public ReportService Reports { get; }
    public LogInboxService Inbox { get; }
    public StorageService Storage { get; }
    public SettingsService Settings { get; }
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
            var seedDirectory = Path.Combine(AppContext.BaseDirectory, "PluginSeed");
            var wizardDataRoot = dataRoot;
            var wizard = new FirstRunWizard(
                dataRoot,
                (root, monitorPaths, progress) =>
                {
                    wizardDataRoot = root;
                    return new WorkbenchInitializationService(seedDirectory).InitializeAsync(root, monitorPaths, progress);
                });
            if (wizard.ShowDialog() != true) return null;
            dataRoot = wizardDataRoot;
            await bootstrapStore.WriteAsync(dataRoot);
        }

        var host = new WorkbenchHost(dataRoot);
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

        await Configuration.EnsureWorkspaceAsync();
        AppSettings = await Configuration.EnsureAppSettingsAsync();
        await Configuration.EnsurePluginConfigAsync();

        Logger.Info("开始登记内置日志分析插件。");
        await new PluginProvisioningService(Paths, _seedDirectory, Logger).ProvisionAsync();
        var bundled = (await PluginCatalog.ScanAsync()).FirstOrDefault(x => string.Equals(x.Id, "log-analyzer", StringComparison.OrdinalIgnoreCase));
        if (bundled is not null)
        {
            await Configuration.UpsertPluginAsync(new HephaestusWorkbench.Core.Models.PluginConfigEntry
            {
                Id = bundled.Id,
                Version = bundled.Version,
                Enabled = true,
                Source = HephaestusWorkbench.Core.Models.PluginInstallSource.Bundled
            });
        }
        Logger.Info("内置日志分析插件登记完成。");

        Logger.Info("开始启动日志收件箱监控。");
        await Inbox.StartAsync();
        Logger.Info(Inbox.IsConfigured
            ? $"日志收件箱监控已启动：{string.Join("、", Inbox.WatchDirectories)}"
            : "未配置日志收件目录，日志收件箱暂不扫描。");

        MainViewModel = new MainViewModel(Analysis, Inbox, Storage, Settings, PluginCatalog, PluginMarketplace, Reports, Logger, ThemeManager.ApplyTheme, Rules, RuleDistribution);
        await MainViewModel.InitializeAsync();
        Logger.Info("工作台初始化完成。");
    }

    public void Dispose()
    {
        MainViewModel?.Dispose();
        Inbox.Dispose();
    }

}
