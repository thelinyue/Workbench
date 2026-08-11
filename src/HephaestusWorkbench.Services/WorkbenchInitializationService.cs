using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Data;

namespace HephaestusWorkbench.Services;

/// <summary>
/// 执行首次初始化和旧版本配置迁移。
/// 所有步骤都设计为可重复执行，向导中途失败后可以安全重试。
/// </summary>
public sealed class WorkbenchInitializationService
{
    private readonly string _seedDirectory;

    public WorkbenchInitializationService(string seedDirectory) => _seedDirectory = seedDirectory;

    public async Task InitializeAsync(
        string dataRoot,
        IEnumerable<string>? monitorPaths = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dataRoot)) throw new ArgumentException("数据目录不能为空。", nameof(dataRoot));

        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot)) + Path.DirectorySeparatorChar;
        var programRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)) + Path.DirectorySeparatorChar;
        if (normalizedRoot.StartsWith(programRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("数据目录不能位于程序安装目录中。");

        var paths = new DataPaths(dataRoot);
        paths.EnsureCreated();
        var logger = new WorkbenchLogger(paths.Root);
        try
        {
            progress?.Report("正在初始化目录…");
            logger.Info("开始初始化工作台目录。");

            var factory = new SqliteConnectionFactory(paths);
            progress?.Report("正在初始化数据库…");
            await new DatabaseInitializer(factory).InitializeAsync(cancellationToken);

            var legacyStore = new SqliteSettingsStore(factory);
            var configuration = new WorkbenchConfigurationService(paths);
            progress?.Report("正在写入工作区配置…");
            await configuration.EnsureWorkspaceAsync(monitorPaths, legacyStore, cancellationToken);
            await configuration.EnsureAppSettingsAsync(legacyStore, cancellationToken);
            await configuration.EnsurePluginConfigAsync(cancellationToken);

            progress?.Report("正在登记内置分析插件…");
            await new PluginProvisioningService(paths, _seedDirectory, logger).ProvisionAsync(cancellationToken);
            var catalog = new PluginCatalog(paths, logger);
            foreach (var plugin in await catalog.ScanAsync(cancellationToken))
            {
                await configuration.UpsertPluginAsync(new PluginConfigEntry
                {
                    Id = plugin.Id,
                    Version = plugin.Version,
                    Enabled = true
                }, cancellationToken);
            }

            logger.Info("工作台首次初始化完成。");
            progress?.Report("初始化完成。");
        }
        catch (Exception ex)
        {
            logger.Error("工作台初始化失败", ex);
            throw;
        }
    }
}
