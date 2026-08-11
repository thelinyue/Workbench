using System.Text.Json;
using System.Text.Json.Serialization;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Repositories;
using HephaestusWorkbench.Data;

namespace HephaestusWorkbench.Services;

/// <summary>
/// 管理工作台的三个 JSON 配置文件。
/// 写入先落临时文件，再替换目标文件，避免进程中断时留下半份配置。
/// </summary>
public sealed class WorkbenchConfigurationService
{
    private readonly DataPaths _paths;
    private readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public WorkbenchConfigurationService(DataPaths paths) => _paths = paths;

    public string DataRoot => _paths.Root;

    public async Task<WorkspaceConfig> EnsureWorkspaceAsync(
        IEnumerable<string>? monitorPaths = null,
        ISettingsStore? legacyStore = null,
        CancellationToken cancellationToken = default)
    {
        _paths.EnsureCreated();
        var existing = await ReadAsync<WorkspaceConfig>(_paths.WorkspaceConfigFile, cancellationToken);
        if (existing is not null)
        {
            existing.DataPath = _paths.Root;
            existing.MonitorPaths = NormalizeMonitorPaths(existing.MonitorPaths);
            await SaveWorkspaceAsync(existing, cancellationToken);
            return existing;
        }

        var legacyWatchDirectory = legacyStore is null
            ? null
            : await legacyStore.GetAsync("watch_directory", cancellationToken);
        var configuredPaths = monitorPaths?.ToArray() ?? Array.Empty<string>();
        var selectedPaths = configuredPaths.Length > 0
            ? configuredPaths
            : string.IsNullOrWhiteSpace(legacyWatchDirectory)
                ? new[] { _paths.InboxDirectory }
                : new[] { legacyWatchDirectory! };

        var created = new WorkspaceConfig
        {
            DataPath = _paths.Root,
            MonitorPaths = NormalizeMonitorPaths(selectedPaths)
        };
        await SaveWorkspaceAsync(created, cancellationToken);
        return created;
    }

    public async Task<AppSettingsConfig> EnsureAppSettingsAsync(
        ISettingsStore? legacyStore = null,
        CancellationToken cancellationToken = default)
    {
        _paths.EnsureCreated();
        var existing = await ReadAsync<AppSettingsConfig>(_paths.AppSettingsFile, cancellationToken);
        if (existing is not null)
        {
            NormalizeAppSettings(existing);
            await SaveAppSettingsAsync(existing, cancellationToken);
            return existing;
        }

        var created = new AppSettingsConfig();
        if (legacyStore is not null)
        {
            var restore = await legacyStore.GetAsync("report_restore_enabled", cancellationToken);
            if (bool.TryParse(restore, out var restoreValue)) created.AutoRestoreReports = restoreValue;

            var maxTabs = await legacyStore.GetAsync("report_max_tabs", cancellationToken);
            if (int.TryParse(maxTabs, out var maxTabValue)) created.MaxReportTabs = maxTabValue;
        }

        NormalizeAppSettings(created);
        await SaveAppSettingsAsync(created, cancellationToken);
        return created;
    }

    public async Task<PluginConfig> EnsurePluginConfigAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureCreated();
        var existing = await ReadAsync<PluginConfig>(_paths.PluginsConfigFile, cancellationToken);
        if (existing is not null)
        {
            existing.Plugins ??= new List<PluginConfigEntry>();
            NormalizePluginConfig(existing);
            await SavePluginConfigAsync(existing, cancellationToken);
            return existing;
        }

        var created = new PluginConfig();
        await SavePluginConfigAsync(created, cancellationToken);
        return created;
    }

    public Task SaveWorkspaceAsync(WorkspaceConfig config, CancellationToken cancellationToken = default)
    {
        config.DataPath = _paths.Root;
        config.MonitorPaths = NormalizeMonitorPaths(config.MonitorPaths);
        return WriteAtomicAsync(_paths.WorkspaceConfigFile, config, cancellationToken);
    }

    public Task SaveAppSettingsAsync(AppSettingsConfig config, CancellationToken cancellationToken = default)
    {
        NormalizeAppSettings(config);
        return WriteAtomicAsync(_paths.AppSettingsFile, config, cancellationToken);
    }

    public Task SavePluginConfigAsync(PluginConfig config, CancellationToken cancellationToken = default)
    {
        config.Plugins ??= new List<PluginConfigEntry>();
        NormalizePluginConfig(config);
        return WriteAtomicAsync(_paths.PluginsConfigFile, config, cancellationToken);
    }

    public async Task UpsertPluginAsync(PluginConfigEntry plugin, CancellationToken cancellationToken = default)
    {
        var config = await EnsurePluginConfigAsync(cancellationToken);
        var existing = config.Plugins.FirstOrDefault(x => string.Equals(x.Id, plugin.Id, StringComparison.OrdinalIgnoreCase));
        if (existing is null) config.Plugins.Add(plugin);
        else
        {
            existing.Version = plugin.Version;
            existing.Source = plugin.Source;
        }
        config.DefaultPluginId ??= plugin.Enabled ? plugin.Id : null;
        await SavePluginConfigAsync(config, cancellationToken);
    }

    private async Task<T?> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return default;
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, _options, cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"配置文件格式错误：{path}", ex);
        }
    }

    private async Task WriteAtomicAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(stream, value, _options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private List<string> NormalizeMonitorPaths(IEnumerable<string>? paths)
    {
        var normalized = (paths ?? Array.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => Path.GetFullPath(x.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return normalized.Count == 0 ? new List<string> { _paths.InboxDirectory } : normalized;
    }

    private static void NormalizeAppSettings(AppSettingsConfig settings)
    {
        settings.Theme = string.Equals(settings.Theme, AppSettingsConfig.DarkTheme, StringComparison.OrdinalIgnoreCase)
            ? AppSettingsConfig.DarkTheme
            : string.Equals(settings.Theme, AppSettingsConfig.LightTheme, StringComparison.OrdinalIgnoreCase)
                ? AppSettingsConfig.LightTheme
                : AppSettingsConfig.LightTheme;
        settings.MaxReportTabs = Math.Clamp(settings.MaxReportTabs, 1, 10);
    }

    private static void NormalizePluginConfig(PluginConfig config)
    {
        config.Plugins = config.Plugins
            .Where(x => !string.IsNullOrWhiteSpace(x.Id))
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToList();
        if (config.DefaultPluginId is not null
            && !config.Plugins.Any(x => x.Enabled && string.Equals(x.Id, config.DefaultPluginId, StringComparison.OrdinalIgnoreCase)))
            config.DefaultPluginId = null;
        config.DefaultPluginId ??= config.Plugins.FirstOrDefault(x => x.Enabled)?.Id;
    }
}
