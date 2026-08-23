using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Data;

namespace HephaestusWorkbench.Services;

/// <summary>
/// 创建全新的 v2 工作区。
/// 初始化前再次执行版本门禁，防止用户在向导中改选旧目录后绕过启动检查。
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

        // 向导允许用户改选目录，因此必须在任何目录、日志或数据库写入前再次执行 v2 门禁。
        var inspection = await new WorkspaceVersionGate().InspectAsync(dataRoot, cancellationToken);
        if (inspection.Status == WorkspaceVersionStatus.Legacy)
            throw new InvalidOperationException($"所选数据目录包含旧版本或无法确认版本的数据，请手工清理后重试：{inspection.DataRoot}");

        var paths = new DataPaths(inspection.DataRoot);
        paths.EnsureCreated();
        var logger = new WorkbenchLogger(paths.Root);
        try
        {
            progress?.Report("正在初始化目录…");
            logger.Info("开始初始化工作台目录。");

            var factory = new SqliteConnectionFactory(paths);
            progress?.Report("正在初始化数据库…");
            await new DatabaseInitializer(factory).InitializeAsync(cancellationToken);

            var configuration = new WorkbenchConfigurationService(paths);
            progress?.Report("正在写入工作区配置…");
            await configuration.EnsureWorkspaceAsync(monitorPaths, cancellationToken);
            await configuration.EnsureAppSettingsAsync(cancellationToken);
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
