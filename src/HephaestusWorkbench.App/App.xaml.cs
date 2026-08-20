using System.Text.Json;
using System.IO;
using System.Windows;
using HephaestusWorkbench.App.ViewModels;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Repositories;
using HephaestusWorkbench.Data;
using HephaestusWorkbench.Services;
using Microsoft.Data.Sqlite;

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
        PluginsRepository = new SqlitePluginInfoRepository(_factory);
        SettingsStore = new SqliteSettingsStore(_factory);
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
        PluginMarketplace = new PluginMarketplaceService(Paths, PluginCatalog, Configuration, TaskCenter, Logger, PluginsRepository);
        Reports = new ReportService(ReportsRepository, Analysis);
        Inbox = new LogInboxService(new LogFileParser(), new ArchiveValidator(), Configuration, Logger, Paths.InboxDirectory);
        Storage = new StorageService(Paths, CasesRepository, Logger);
        Settings = new SettingsService(Configuration, SettingsStore, Paths.InboxDirectory);
    }

    public DataPaths Paths { get; }
    public WorkbenchLogger Logger { get; }
    public IAnalysisCaseRepository CasesRepository { get; }
    public IAnalysisTaskRepository TasksRepository { get; }
    public IReportRepository ReportsRepository { get; }
    public IPluginInfoRepository PluginsRepository { get; }
    public ISettingsStore SettingsStore { get; }
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
        var bootstrapFile = Path.Combine(bootstrapDirectory, "bootstrap.json");
        var dataRoot = LoadDataRoot(bootstrapFile);
        var seedDirectory = Path.Combine(AppContext.BaseDirectory, "PluginSeed");
        var databaseExists = !string.IsNullOrWhiteSpace(dataRoot)
            && File.Exists(Path.Combine(dataRoot!, "Database", "workbench.db"));
        if (!databaseExists)
        {
            var selectedRoot = dataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "HephaestusWorkbenchData");
            var wizardDataRoot = selectedRoot;
            var wizard = new FirstRunWizard(
                selectedRoot,
                (root, monitorPaths, progress) =>
                {
                    wizardDataRoot = root;
                    return new WorkbenchInitializationService(seedDirectory).InitializeAsync(root, monitorPaths, progress);
                });
            if (wizard.ShowDialog() != true) return null;
            // 向导初始化完成后写入引导指针，后续启动无需再次询问数据目录。
            dataRoot = wizardDataRoot;
            WriteBootstrap(bootstrapFile, dataRoot);
        }

        var host = new WorkbenchHost(dataRoot!);
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

    private static void WriteBootstrap(string file, string dataRoot)
    {
        var temporary = file + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new BootstrapSettings(dataRoot), new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(file)) File.Replace(temporary, file, null);
            else File.Move(temporary, file);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private async Task InitializeAsync()
    {
        // 所有可能产生异步 IO 的启动步骤集中在这里，并由 OnStartup 使用 await 调用。
        Logger.Info("开始初始化工作台数据库。");
        try
        {
            await new DatabaseInitializer(_factory).InitializeAsync();
        }
        catch (SqliteException ex) when (File.Exists(Paths.DatabaseFile))
        {
            var backup = Paths.DatabaseFile + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}";
            File.Copy(Paths.DatabaseFile, backup, overwrite: true);
            Logger.Error($"检测到数据库损坏，旧数据库已备份到：{backup}", ex);
            var choice = System.Windows.MessageBox.Show(
                $"数据库无法打开，已备份旧文件。\n\n是否创建新的数据库继续运行？\n\n备份：{backup}",
                "Hephaestus工作台数据恢复",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (choice != MessageBoxResult.Yes) throw new InvalidOperationException("用户取消数据库恢复。", ex);
            File.Delete(Paths.DatabaseFile);
            await new DatabaseInitializer(_factory).InitializeAsync();
            Logger.Info("已创建新的工作台数据库。");
        }
        Logger.Info("工作台数据库初始化完成。");
        var recoveredTasks = await LifecycleRepository.RecoverInterruptedAsync(DateTime.Now);
        if (recoveredTasks > 0)
            Logger.Info($"已恢复上次未完成的分析任务：{recoveredTasks} 个。");

        await Configuration.EnsureWorkspaceAsync(legacyStore: SettingsStore);
        AppSettings = await Configuration.EnsureAppSettingsAsync(SettingsStore);
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
        await PluginMarketplace.SynchronizePluginInfoAsync();
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

    private static string? LoadDataRoot(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            return JsonSerializer.Deserialize<BootstrapSettings>(File.ReadAllText(file))?.DataRoot;
        }
        catch { return null; }
    }

    public void Dispose()
    {
        MainViewModel?.Dispose();
        Inbox.Dispose();
    }

    private sealed record BootstrapSettings(string DataRoot);
}
