using HephaestusWorkbench.Core.Models;

namespace HephaestusWorkbench.Services;

/// <summary>
/// 统一读写 v2 工作区和应用偏好。
/// 所有设置只通过 schema v2 JSON 配置持久化，不读取或回退到 SQLite 键值。
/// </summary>
public sealed class SettingsService
{
    private readonly WorkbenchConfigurationService _configuration;
    private readonly string _defaultWatchDirectory;

    public SettingsService(WorkbenchConfigurationService configuration, string defaultWatchDirectory)
    {
        _configuration = configuration;
        _defaultWatchDirectory = Path.GetFullPath(defaultWatchDirectory);
    }

    public async Task<IReadOnlyList<string>> GetWatchDirectoriesAsync(CancellationToken cancellationToken = default)
    {
        var workspace = await _configuration.EnsureWorkspaceAsync(cancellationToken: cancellationToken);
        return workspace.MonitorPaths;
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
        await _configuration.SaveWorkspaceAsync(new WorkspaceConfig
        {
            DataPath = _configuration.DataRoot,
            MonitorPaths = normalized.ToList()
        }, cancellationToken);
    }
    public async Task<int> GetCleanupRetentionDaysAsync(CancellationToken cancellationToken = default)
    {
        return (await _configuration.EnsureAppSettingsAsync(cancellationToken)).CleanupRetentionDays;
    }

    public async Task SetCleanupRetentionDaysAsync(int value, CancellationToken cancellationToken = default)
    {
        if (value is < 1 or > 7) throw new ArgumentOutOfRangeException(nameof(value), "清理保留天数必须在 1 到 7 天之间。");
        var settings = await _configuration.EnsureAppSettingsAsync(cancellationToken);
        settings.CleanupRetentionDays = value;
        await _configuration.SaveAppSettingsAsync(settings, cancellationToken);
    }

    /// <summary>
    /// 读取 appsettings.json 中的界面主题。
    /// </summary>
    public async Task<string> GetThemeAsync(CancellationToken cancellationToken = default)
    {
        return (await _configuration.EnsureAppSettingsAsync(cancellationToken)).Theme;
    }

    /// <summary>
    /// 保存界面主题。非法值统一回退为亮色，避免配置文件写入无法加载的主题名称。
    /// </summary>
    public async Task SetThemeAsync(string theme, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeTheme(theme);
        var settings = await _configuration.EnsureAppSettingsAsync(cancellationToken);
        settings.Theme = normalized;
        await _configuration.SaveAppSettingsAsync(settings, cancellationToken);
    }

    public async Task<bool> GetExtensionAutoCheckUpdatesAsync(CancellationToken cancellationToken = default)
        => (await _configuration.EnsureAppSettingsAsync(cancellationToken)).Extension.AutoCheckUpdates;

    public async Task SetExtensionAutoCheckUpdatesAsync(
        bool autoCheckUpdates,
        CancellationToken cancellationToken = default)
    {
        var settings = await _configuration.EnsureAppSettingsAsync(cancellationToken);
        settings.Extension.AutoCheckUpdates = autoCheckUpdates;
        await _configuration.SaveAppSettingsAsync(settings, cancellationToken);
    }

    public async Task<bool> GetExtensionAllowPrereleaseAsync(CancellationToken cancellationToken = default)
        => (await _configuration.EnsureAppSettingsAsync(cancellationToken)).Extension.AllowPrerelease;

    public async Task SetExtensionAllowPrereleaseAsync(
        bool allowPrerelease,
        CancellationToken cancellationToken = default)
    {
        var settings = await _configuration.EnsureAppSettingsAsync(cancellationToken);
        settings.Extension.AllowPrerelease = allowPrerelease;
        await _configuration.SaveAppSettingsAsync(settings, cancellationToken);
    }

    public async Task<SshTerminalPreferences> GetSshTerminalPreferencesAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _configuration.EnsureAppSettingsAsync(cancellationToken);
        return new SshTerminalPreferences(
            settings.Ssh.DefaultPort,
            settings.Terminal.FontFamily,
            settings.Terminal.FontSize,
            settings.ReconnectBehavior);
    }

    public async Task SetSshTerminalPreferencesAsync(
        int defaultPort,
        string fontFamily,
        double fontSize,
        SshReconnectBehavior reconnectBehavior,
        CancellationToken cancellationToken = default)
    {
        if (defaultPort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(defaultPort), "SSH 默认端口必须在 1 到 65535 之间。");
        if (string.IsNullOrWhiteSpace(fontFamily))
            throw new ArgumentException("终端字体不能为空。", nameof(fontFamily));
        if (fontSize is < 10 or > 24)
            throw new ArgumentOutOfRangeException(nameof(fontSize), "终端字号必须在 10 到 24 之间。");
        if (!Enum.IsDefined(reconnectBehavior))
            throw new ArgumentOutOfRangeException(nameof(reconnectBehavior), "SSH 重连策略无效。");

        var settings = await _configuration.EnsureAppSettingsAsync(cancellationToken);
        settings.Ssh.DefaultPort = defaultPort;
        settings.Terminal.FontFamily = fontFamily.Trim();
        settings.Terminal.FontSize = fontSize;
        settings.ReconnectBehavior = reconnectBehavior;
        await _configuration.SaveAppSettingsAsync(settings, cancellationToken);
    }

    private static string NormalizeTheme(string? theme)
        => string.Equals(theme, AppSettingsConfig.DarkTheme, StringComparison.OrdinalIgnoreCase)
            ? AppSettingsConfig.DarkTheme
            : AppSettingsConfig.LightTheme;
}
