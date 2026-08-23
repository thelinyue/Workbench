using System.Text.Json;
using System.Text.Json.Serialization;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Data;

namespace HephaestusWorkbench.Services;

/// <summary>
/// 管理 workspace.json 与 appsettings.json 两份基础配置；extensions.json 由 ExtensionSettingsStore 独立管理。
/// 写入先落临时文件，再替换目标文件，避免进程中断时留下半份配置。
/// </summary>
public sealed class WorkbenchConfigurationService
{
    private readonly DataPaths _paths;
    private readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public WorkbenchConfigurationService(DataPaths paths) => _paths = paths;

    public string DataRoot => _paths.Root;

    public async Task<WorkspaceConfig> EnsureWorkspaceAsync(
        IEnumerable<string>? monitorPaths = null,
        CancellationToken cancellationToken = default)
    {
        _paths.EnsureCreated();
        var existing = await ReadAsync<WorkspaceConfig>(_paths.WorkspaceConfigFile, cancellationToken);
        if (existing is not null)
        {
            ValidateSchema(existing.SchemaVersion, _paths.WorkspaceConfigFile);
            existing.DataPath = _paths.Root;
            existing.MonitorPaths = NormalizeMonitorPaths(existing.MonitorPaths);
            await SaveWorkspaceAsync(existing, cancellationToken);
            return existing;
        }

        // v2.0.0 是全新正式版工作区，不读取或迁移旧版 SQLite 设置。
        var configuredPaths = monitorPaths?.ToArray() ?? Array.Empty<string>();
        var selectedPaths = configuredPaths.Length > 0
            ? configuredPaths
            : new[] { _paths.InboxDirectory };

        var created = new WorkspaceConfig
        {
            DataPath = _paths.Root,
            MonitorPaths = NormalizeMonitorPaths(selectedPaths)
        };
        await SaveWorkspaceAsync(created, cancellationToken);
        return created;
    }

    public async Task<AppSettingsConfig> EnsureAppSettingsAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureCreated();
        var existing = await ReadAsync<AppSettingsConfig>(_paths.AppSettingsFile, cancellationToken);
        if (existing is not null)
        {
            ValidateSchema(existing.SchemaVersion, _paths.AppSettingsFile);
            NormalizeAppSettings(existing);
            await SaveAppSettingsAsync(existing, cancellationToken);
            return existing;
        }

        // 正式版不读取旧数据库中的偏好设置。
        var created = new AppSettingsConfig();
        NormalizeAppSettings(created);
        await SaveAppSettingsAsync(created, cancellationToken);
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

    private static void ValidateSchema(int schemaVersion, string path)
    {
        if (schemaVersion != 2)
            throw new InvalidDataException($"配置文件 schemaVersion 必须为 2：{path}");
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
        settings.CleanupRetentionDays = Math.Clamp(settings.CleanupRetentionDays, 1, 7);
    }

}
