using HephaestusWorkbench.Data;

namespace HephaestusWorkbench.Services;

/// <summary>
/// 将随程序发布的现有 log_analyzer.exe 复制到用户数据目录，保证程序目录和案例数据分离。
/// </summary>
public sealed class PluginProvisioningService
{
    private readonly DataPaths _paths;
    private readonly string _seedDirectory;
    private readonly WorkbenchLogger _logger;

    public PluginProvisioningService(DataPaths paths, string seedDirectory, WorkbenchLogger logger)
    {
        _paths = paths;
        _seedDirectory = seedDirectory;
        _logger = logger;
    }

    public Task ProvisionAsync(CancellationToken cancellationToken = default)
    {
        var sourceExe = Path.Combine(_seedDirectory, "log_analyzer.exe");
        var sourceManifest = Path.Combine(_seedDirectory, "manifest.json");
        if (!File.Exists(sourceExe) || !File.Exists(sourceManifest))
        {
            _logger.Error($"未找到内置日志分析插件：{_seedDirectory}");
            return Task.CompletedTask;
        }

        var destination = Path.Combine(_paths.PluginsDirectory, "log-analyzer");
        Directory.CreateDirectory(destination);
        var destinationExe = Path.Combine(destination, "log_analyzer.exe");
        var destinationManifest = Path.Combine(destination, "manifest.json");
        if (!File.Exists(destinationExe) || new FileInfo(destinationExe).Length != new FileInfo(sourceExe).Length)
            File.Copy(sourceExe, destinationExe, overwrite: true);
        File.Copy(sourceManifest, destinationManifest, overwrite: true);
        _logger.Info("现有日志分析插件已登记到用户插件目录。");
        return Task.CompletedTask;
    }
}
