using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Repositories;

namespace HephaestusWorkbench.Services;

/// <summary>
/// 统一读写工作区和应用偏好。
/// JSON 是新版本的主配置来源，SQLite 键值只作为旧版本兼容镜像保留。
/// </summary>
public sealed class SettingsService
{
    private const int DefaultMaxOpenReports = 10;
    private readonly ISettingsStore _store;
    private readonly WorkbenchConfigurationService? _configuration;
    private readonly string _defaultWatchDirectory;

    public SettingsService(ISettingsStore store, string defaultWatchDirectory)
    {
        _store = store;
        _defaultWatchDirectory = defaultWatchDirectory;
    }

    public SettingsService(
        WorkbenchConfigurationService configuration,
        ISettingsStore store,
        string defaultWatchDirectory)
    {
        _configuration = configuration;
        _store = store;
        _defaultWatchDirectory = defaultWatchDirectory;
    }

    public async Task<IReadOnlyList<string>> GetWatchDirectoriesAsync(CancellationToken cancellationToken = default)
    {
        if (_configuration is not null)
        {
            var workspace = await _configuration.EnsureWorkspaceAsync(cancellationToken: cancellationToken);
            return workspace.MonitorPaths;
        }

        var legacy = await _store.GetAsync("watch_directory", cancellationToken);
        return string.IsNullOrWhiteSpace(legacy)
            ? new[] { Path.GetFullPath(_defaultWatchDirectory) }
            : new[] { Path.GetFullPath(legacy!) };
    }

    public async Task<string> GetWatchDirectoryAsync(CancellationToken cancellationToken = default)
        => (await GetWatchDirectoriesAsync(cancellationToken)).FirstOrDefault()
           ?? Path.GetFullPath(_defaultWatchDirectory);

    public Task SetWatchDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => SetWatchDirectoriesAsync(new[] { path }, cancellationToken);

    public async Task SetWatchDirectoriesAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        var normalized = paths
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => Path.GetFullPath(x.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalized.Length == 0) throw new ArgumentException("至少需要一个日志监控目录。", nameof(paths));

        foreach (var path in normalized) Directory.CreateDirectory(path);
        if (_configuration is not null)
        {
            await _configuration.SaveWorkspaceAsync(new WorkspaceConfig
            {
                DataPath = _configuration.DataRoot,
                MonitorPaths = normalized.ToList()
            }, cancellationToken);
            return;
        }

        await _store.SetAsync("watch_directory", normalized[0], cancellationToken);
    }

    public async Task<bool> GetReportRestoreEnabledAsync(CancellationToken cancellationToken = default)
    {
        if (_configuration is not null)
            return (await _configuration.EnsureAppSettingsAsync(_store, cancellationToken)).AutoRestoreReports;

        return !bool.TryParse(await _store.GetAsync("report_restore_enabled", cancellationToken), out var enabled) || enabled;
    }

    public async Task SetReportRestoreEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (_configuration is not null)
        {
            var settings = await _configuration.EnsureAppSettingsAsync(_store, cancellationToken);
            settings.AutoRestoreReports = enabled;
            await _configuration.SaveAppSettingsAsync(settings, cancellationToken);
            return;
        }
        await _store.SetAsync("report_restore_enabled", enabled.ToString(), cancellationToken);
    }

    public async Task<int> GetReportMaxTabsAsync(CancellationToken cancellationToken = default)
    {
        if (_configuration is not null)
            return (await _configuration.EnsureAppSettingsAsync(_store, cancellationToken)).MaxReportTabs;

        var raw = await _store.GetAsync("report_max_tabs", cancellationToken);
        return int.TryParse(raw, out var value) ? Math.Clamp(value, 1, DefaultMaxOpenReports) : DefaultMaxOpenReports;
    }

    public async Task SetReportMaxTabsAsync(int value, CancellationToken cancellationToken = default)
    {
        if (value is < 1 or > DefaultMaxOpenReports) throw new ArgumentOutOfRangeException(nameof(value), "最大报告数量必须在 1 到 10 之间。");
        if (_configuration is not null)
        {
            var settings = await _configuration.EnsureAppSettingsAsync(_store, cancellationToken);
            settings.MaxReportTabs = value;
            await _configuration.SaveAppSettingsAsync(settings, cancellationToken);
            return;
        }
        await _store.SetAsync("report_max_tabs", value.ToString(), cancellationToken);
    }

    public async Task<bool> GetManualCleanupEnabledAsync(CancellationToken cancellationToken = default)
    {
        if (_configuration is not null)
            return (await _configuration.EnsureAppSettingsAsync(_store, cancellationToken)).ManualCleanupEnabled;

        return bool.TryParse(await _store.GetAsync("manual_cleanup_enabled", cancellationToken), out var enabled) && enabled;
    }

    public async Task SetManualCleanupEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (_configuration is not null)
        {
            var settings = await _configuration.EnsureAppSettingsAsync(_store, cancellationToken);
            settings.ManualCleanupEnabled = enabled;
            await _configuration.SaveAppSettingsAsync(settings, cancellationToken);
            return;
        }
        await _store.SetAsync("manual_cleanup_enabled", enabled.ToString(), cancellationToken);
    }

    public async Task<int> GetCleanupRetentionDaysAsync(CancellationToken cancellationToken = default)
    {
        if (_configuration is not null)
            return (await _configuration.EnsureAppSettingsAsync(_store, cancellationToken)).CleanupRetentionDays;

        var raw = await _store.GetAsync("cleanup_retention_days", cancellationToken);
        return int.TryParse(raw, out var value) ? Math.Clamp(value, 1, 7) : 7;
    }

    public async Task SetCleanupRetentionDaysAsync(int value, CancellationToken cancellationToken = default)
    {
        if (value is < 1 or > 7) throw new ArgumentOutOfRangeException(nameof(value), "清理保留天数必须在 1 到 7 天之间。");
        if (_configuration is not null)
        {
            var settings = await _configuration.EnsureAppSettingsAsync(_store, cancellationToken);
            settings.CleanupRetentionDays = value;
            await _configuration.SaveAppSettingsAsync(settings, cancellationToken);
            return;
        }
        await _store.SetAsync("cleanup_retention_days", value.ToString(), cancellationToken);
    }

    /// <summary>
    /// 读取界面主题。新配置以 appsettings.json 为准，旧版键值仅作为兼容读取来源。
    /// </summary>
    public async Task<string> GetThemeAsync(CancellationToken cancellationToken = default)
    {
        if (_configuration is not null)
            return (await _configuration.EnsureAppSettingsAsync(_store, cancellationToken)).Theme;

        var legacy = await _store.GetAsync("theme", cancellationToken);
        return NormalizeTheme(legacy);
    }

    /// <summary>
    /// 保存界面主题。非法值统一回退为亮色，避免配置文件写入无法加载的主题名称。
    /// </summary>
    public async Task SetThemeAsync(string theme, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeTheme(theme);
        if (_configuration is not null)
        {
            var settings = await _configuration.EnsureAppSettingsAsync(_store, cancellationToken);
            settings.Theme = normalized;
            await _configuration.SaveAppSettingsAsync(settings, cancellationToken);
            return;
        }

        await _store.SetAsync("theme", normalized, cancellationToken);
    }

    /// <summary>读取应用级 GitHub 插件下载加速模板。</summary>
    public async Task<string> GetGitHubDownloadMirrorTemplateAsync(CancellationToken cancellationToken = default)
    {
        if (_configuration is not null)
            return (await _configuration.EnsureAppSettingsAsync(_store, cancellationToken)).GitHubDownloadMirrorTemplate;

        return (await _store.GetAsync("github_download_mirror_template", cancellationToken))?.Trim() ?? string.Empty;
    }

    /// <summary>
    /// 保存 GitHub 插件下载加速模板。空值会停用加速；非空值必须是 HTTPS 模板并且只包含一个 {url}。
    /// </summary>
    public async Task SetGitHubDownloadMirrorTemplateAsync(string? template, CancellationToken cancellationToken = default)
    {
        var normalized = GitHubDownloadMirrorTemplate.ValidateAndNormalize(template);
        if (_configuration is not null)
        {
            var settings = await _configuration.EnsureAppSettingsAsync(_store, cancellationToken);
            settings.GitHubDownloadMirrorTemplate = normalized;
            await _configuration.SaveAppSettingsAsync(settings, cancellationToken);
            return;
        }

        await _store.SetAsync("github_download_mirror_template", normalized, cancellationToken);
    }

    private static string NormalizeTheme(string? theme)
        => string.Equals(theme, AppSettingsConfig.DarkTheme, StringComparison.OrdinalIgnoreCase)
            ? AppSettingsConfig.DarkTheme
            : AppSettingsConfig.LightTheme;
}
